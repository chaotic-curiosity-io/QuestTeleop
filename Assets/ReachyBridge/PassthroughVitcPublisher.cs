// ReachyBrain XR bridge — Quest port. Passthrough camera -> VITC publisher.
//
// The Viture app streamed its middle RGB camera to the Mac as MJPEG over the
// "VITC" wire protocol on TCP :9901; the Mac's integrations/viture/map_capture.py
// pulls that stream during a walk-capture (Unity "B") and reconstructs the space
// on the remote lingbot-map backend, merging it into the persistent home map.
//
// This reproduces that exact wire format from the Quest passthrough camera
// (MRUK PassthroughCameraAccess), so the Mac side needs NO format change — only
// to be pointed at this headset's IP instead of 127.0.0.1 (the server resolves
// that automatically from the XR client's peer address; see server.py
// --middle-host auto).
//
// Wire format (matches integrations/viture/map_capture.py _VITC_HDR):
//   little-endian struct "<IIIIIIIQ" (36 bytes):
//     magic=0x56495443 | stream_id=0 (middle) | format=0 (MJPEG) |
//     width | height | payload_size | seq | timestamp_ns
//   followed by <payload_size> bytes of JPEG.
//
// Frames are only grabbed/encoded while a client is connected (the Mac connects
// only for the duration of a walk-capture), so this costs nothing when idle.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Meta.XR;
using UnityEngine;

namespace ReachyBridge
{
    public class PassthroughVitcPublisher : MonoBehaviour
    {
        [Header("Source (auto-found if left empty)")]
        public PassthroughCameraAccess cameraAccess;

        [Header("Publishing")]
        public int port = 9901;
        [Tooltip("Frames per second to encode & publish while a client is connected.")]
        [Range(1f, 30f)] public float captureFps = 10f;
        [Range(1, 100)] public int jpegQuality = 75;
        [Tooltip("Downscale width for the JPEG (0 = native camera resolution). " +
                 "Lower = cheaper CPU; the reconstruction is fine at ~640-960.")]
        public int captureWidth = 640;
        [Tooltip("Flip the image vertically before encoding (toggle if the " +
                 "reconstruction comes out upside down).")]
        public bool flipVertical = false;

        const uint VitcMagic = 0x56495443;   // 'VITC'
        const uint StreamMiddle = 0;
        const uint FormatMjpeg = 0;

        TcpListener _listener;
        Thread _acceptThread;
        Thread _sendThread;
        volatile bool _running;
        readonly List<TcpClient> _clients = new();
        readonly object _clientsLock = new();
        readonly BlockingCollection<byte[]> _outbox = new(boundedCapacity: 8);

        // main-thread frame grab -> reusable buffers
        RenderTexture _rt;
        Texture2D _readback;
        float _accum;
        uint _seq;

        public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

        void Start()
        {
            if (cameraAccess == null) cameraAccess = FindObjectOfType<PassthroughCameraAccess>();
            StartServer();
        }

        void OnDestroy() => StopServer();
        void OnApplicationQuit() => StopServer();

        void StartServer()
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "VitcAccept" };
            _acceptThread.Start();
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "VitcSend" };
            _sendThread.Start();
            Debug.Log($"[VitcPublisher] listening on :{port}");
        }

        void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            lock (_clientsLock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
            _outbox.CompleteAdding();
        }

        void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    lock (_clientsLock) _clients.Add(client);
                    Debug.Log($"[VitcPublisher] client connected ({ClientCount} total)");
                }
                catch { if (_running) Thread.Sleep(200); }
            }
        }

        void SendLoop()
        {
            foreach (var packet in _outbox.GetConsumingEnumerable())
            {
                List<TcpClient> snapshot;
                lock (_clientsLock) snapshot = new List<TcpClient>(_clients);
                foreach (var c in snapshot)
                {
                    try
                    {
                        c.GetStream().Write(packet, 0, packet.Length);
                    }
                    catch
                    {
                        lock (_clientsLock) _clients.Remove(c);
                        try { c.Close(); } catch { }
                    }
                }
            }
        }

        // ------------------------------------------------------------------ //
        // Main-thread frame grab (Unity texture APIs are main-thread only)     //
        // ------------------------------------------------------------------ //

        void Update()
        {
            if (ClientCount == 0) return;                 // nobody recording -> idle
            if (cameraAccess == null || !cameraAccess.IsPlaying) return;

            _accum += Time.deltaTime;
            float period = 1f / Mathf.Max(captureFps, 1f);
            if (_accum < period) return;
            _accum = 0f;

            if (!cameraAccess.IsUpdatedThisFrame) return;
            var src = cameraAccess.GetTexture();
            if (src == null) return;

            var packet = GrabAndEncode(src);
            if (packet != null && !_outbox.IsAddingCompleted)
            {
                // Drop the oldest if the encoder outruns the socket.
                if (!_outbox.TryAdd(packet))
                {
                    _outbox.TryTake(out _);
                    _outbox.TryAdd(packet);
                }
            }
        }

        byte[] GrabAndEncode(Texture src)
        {
            int w = captureWidth > 0 ? captureWidth : src.width;
            int h = captureWidth > 0 ? Mathf.RoundToInt(src.height * (float)captureWidth / src.width) : src.height;

            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                _readback = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }

            var prev = RenderTexture.active;
            Graphics.Blit(src, _rt);
            RenderTexture.active = _rt;
            _readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            _readback.Apply(false);
            RenderTexture.active = prev;

            if (flipVertical) FlipVertical(_readback);

            byte[] jpeg = _readback.EncodeToJPG(jpegQuality);
            return BuildVitcPacket(jpeg, w, h);
        }

        static void FlipVertical(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = y * w + x, b = (h - 1 - y) * w + x;
                    (px[a], px[b]) = (px[b], px[a]);
                }
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        byte[] BuildVitcPacket(byte[] jpeg, int w, int h)
        {
            // BinaryWriter is always little-endian, matching Python struct "<...".
            using var ms = new MemoryStream(36 + jpeg.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(VitcMagic);
            bw.Write(StreamMiddle);
            bw.Write(FormatMjpeg);
            bw.Write((uint)w);
            bw.Write((uint)h);
            bw.Write((uint)jpeg.Length);
            bw.Write(unchecked(_seq++));
            bw.Write((ulong)(Time.unscaledTimeAsDouble * 1e9));
            bw.Write(jpeg);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
