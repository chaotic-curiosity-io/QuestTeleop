// VLA XR teleop — robot camera video overlay.
//
// Receives the robot's camera view streamed by DevVLA's RobotCameraStreamer
// (UDP :9908, one frame per datagram: "VLAC" + uint32 LE seq + JPEG) and
// renders it on a panel anchored in front of the user. The panel lazily
// follows the gaze (smoothed head-locked), so the robot's view is always
// visible without being nailed rigidly to the head.
//
// DevVLA learns where to send from the xr_pose stream this headset already
// emits (VlaTeleopSender -> :9906), so as long as teleop is running the video
// finds its way back with zero IP configuration.
//
// Enable via Tools > Robot Teleop > Camera Stream > Enable Robot Camera
// Overlay (CameraStreamMenu.cs). QuestTeleop is BUILT-IN RP — the panel uses
// Unlit/Texture.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class RobotCameraOverlay : MonoBehaviour
    {
        [Header("Network")]
        [Tooltip("UDP port DevVLA's RobotCameraStreamer sends JPEG frames to.")]
        public int listenPort = 9908;

        [Header("Panel")]
        [Tooltip("Head pose source. Auto: OVRCameraRig.centerEyeAnchor, else Camera.main.")]
        public Transform headTransform;
        [Tooltip("Meters in front of the head.")]
        public float distance = 1.2f;
        [Tooltip("Vertical offset from gaze center. POSITIVE (above the eye " +
                 "line) by default: the robot's point cloud materializes at " +
                 "roughly body height straight ahead, so a panel sitting below " +
                 "the gaze centre ends up inside it.")]
        public float heightOffset = 0.28f;
        [Tooltip("Draw the panel over everything (ZTest Always). Belt and " +
                 "braces with heightOffset — the cloud is a volume, so no fixed " +
                 "offset is safe at every distance, but a panel that ignores " +
                 "depth can never be swallowed by it.")]
        public bool alwaysOnTop = true;
        [Tooltip("Panel width in meters; height follows the frame's aspect ratio.")]
        public float panelWidth = 0.55f;
        [Tooltip("Follow smoothing rates (1/s). Higher = more head-locked.")]
        public float positionLerp = 6f;
        public float rotationLerp = 8f;

        [Header("Debug")]
        public bool showHud = true;
        [Tooltip("Seconds between heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;

        UdpClient _udp;
        Thread _thread;
        volatile bool _stop;
        readonly object _lock = new object();
        byte[] _latestPacket;
        int _received;

        int _consumed;
        Texture2D _tex;
        Transform _panel;
        Material _mat;
        uint _lastSeq;
        bool _haveSeq;
        int _drops;
        float _lastFrameAt = -1f;
        int _rateCount; float _rateWindowStart; int _rate;   // frames/s, 1 s bucket
        float _nextBeat; int _beatFrames;

        /// <summary>Latest decoded video frame (null until one arrives).
        /// RobotPointCloudOverlay samples this to color the point cloud.</summary>
        public Texture2D LatestTexture => _haveSeq ? _tex : null;

        /// <summary>Sequence of the latest decoded frame.</summary>
        public uint LastSeq => _lastSeq;

        void Start()
        {
            if (headTransform == null)
            {
                var rig = FindObjectOfType<OVRCameraRig>();
                if (rig != null) headTransform = rig.centerEyeAnchor;
            }
            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;

            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));
                _udp.Client.ReceiveTimeout = 500;
            }
            catch (SocketException e)
            {
                Debug.LogError($"[RobotCamOverlay] Cannot bind UDP :{listenPort} " +
                               $"({e.SocketErrorCode}) — is another overlay running?");
                enabled = false;
                return;
            }
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "robot-cam-udp" };
            _thread.Start();

            BuildPanel();
            Debug.Log($"[RobotCamOverlay] Listening for VLAC frames on udp :{listenPort}. " +
                      "DevVLA side: VLA Control Panel > Teleop Mode > Enable Robot Camera Stream.");
        }

        void OnDestroy()
        {
            _stop = true;
            try { _udp?.Close(); } catch { }
            if (_panel != null) Destroy(_panel.gameObject);
            if (_mat != null) Destroy(_mat);
            if (_tex != null) Destroy(_tex);
        }

        /// <summary>Build the video quad. With ``alwaysOnTop`` it uses the
        /// depth-ignoring shader in Resources (built-in RP, ZTest Always +
        /// Overlay queue) so the point cloud cannot draw over it; otherwise the
        /// stock Unlit/Texture, which depth-sorts normally.</summary>
        void BuildPanel()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "RobotCameraPanel";
            DestroyAny(go.GetComponent<Collider>());
            _mat = new Material(PanelShader());
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _panel = go.transform;
            _panel.SetParent(transform, false);
            _panel.gameObject.SetActive(false);           // hidden until a frame arrives
            _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _mat.mainTexture = _tex;
        }

        /// <summary>Shader the panel is actually using (diagnostics / tests).</summary>
        public string PanelShaderName => _mat != null && _mat.shader != null
            ? _mat.shader.name : "<none>";

        Shader PanelShader()
        {
            if (alwaysOnTop)
            {
                var s = Resources.Load<Shader>("RobotCameraPanel");
                if (s != null) return s;
                Debug.LogWarning("[RobotCamOverlay] Resources/RobotCameraPanel.shader " +
                                 "missing — falling back to Unlit/Texture, which the " +
                                 "point cloud can occlude.");
            }
            return Shader.Find("Unlit/Texture");
        }

        static void DestroyAny(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>Editor/headless entry: build the panel without the network
        /// thread, and show a texture directly. Lets the harness prove the
        /// panel's placement and occlusion behaviour with no UDP.</summary>
        public Transform ShowTextureDirect(Texture2D frame)
        {
            if (_panel == null) BuildPanel();
            if (frame == null) return _panel;
            _mat.mainTexture = frame;
            _haveSeq = true;
            _panel.gameObject.SetActive(true);
            float aspect = frame.height / (float)Mathf.Max(1, frame.width);
            _panel.localScale = new Vector3(panelWidth, panelWidth * aspect, 1f);
            return _panel;
        }

        /// <summary>Rebuild the quad so a changed ``alwaysOnTop`` takes effect
        /// (the shader is chosen at build time). Editor/headless only — at
        /// runtime the setting is read once at Start.</summary>
        public void RebuildPanelForTest()
        {
            if (_panel != null) DestroyAny(_panel.gameObject);
            if (_mat != null) DestroyAny(_mat);
            _panel = null;
            _mat = null;
            BuildPanel();
        }

        /// <summary>Snap the panel to its target pose immediately (no lazy
        /// follow) — used on Recenter and by headless tests.</summary>
        [ContextMenu("Recenter")]
        public void Recenter()
        {
            if (_panel == null || headTransform == null) return;
            _panel.SetPositionAndRotation(TargetPos(), TargetRot());
        }

        Vector3 TargetPos() =>
            headTransform.position + headTransform.forward * distance
            + Vector3.up * heightOffset;

        Quaternion TargetRot() =>
            Quaternion.LookRotation(TargetPos() - headTransform.position);

        void ReceiveLoop()
        {
            IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                byte[] data;
                try { data = _udp.Receive(ref any); }
                catch (SocketException) { continue; }        // timeout — poll _stop
                catch (ObjectDisposedException) { return; }
                if (data == null || data.Length <= 8 ||
                    data[0] != 'V' || data[1] != 'L' || data[2] != 'A' || data[3] != 'C')
                    continue;
                lock (_lock)
                {
                    _latestPacket = data;
                    _received++;
                }
            }
        }

        void Update()
        {
            byte[] packet = null;
            lock (_lock)
            {
                if (_received != _consumed)
                {
                    packet = _latestPacket;
                    _consumed = _received;
                }
            }
            if (packet != null) ApplyFrame(packet);

            if (Time.unscaledTime - _rateWindowStart >= 1f)
            {
                _rate = _rateCount;
                _rateCount = 0;
                _rateWindowStart = Time.unscaledTime;
            }
            Heartbeat();
        }

        void ApplyFrame(byte[] packet)
        {
            uint seq = (uint)(packet[4] | packet[5] << 8 | packet[6] << 16 | packet[7] << 24);
            if (_haveSeq && seq > _lastSeq + 1) _drops += (int)(seq - _lastSeq - 1);
            _lastSeq = seq;
            _haveSeq = true;

            var jpeg = new byte[packet.Length - 8];
            Buffer.BlockCopy(packet, 8, jpeg, 0, jpeg.Length);
            if (!_tex.LoadImage(jpeg)) return;              // resizes the texture itself

            if (!_panel.gameObject.activeSelf) _panel.gameObject.SetActive(true);
            float aspect = _tex.height / (float)Mathf.Max(1, _tex.width);
            _panel.localScale = new Vector3(panelWidth, panelWidth * aspect, 1f);

            _lastFrameAt = Time.unscaledTime;
            _rateCount++;
            _beatFrames++;
        }

        void LateUpdate()
        {
            if (_panel == null || headTransform == null || !_panel.gameObject.activeSelf)
                return;
            // Quad's visible face is -Z: pointing +Z away from the head shows
            // the texture to the user, un-mirrored.
            Vector3 targetPos = TargetPos();
            Quaternion targetRot = TargetRot();

            float dt = Time.deltaTime;
            _panel.position = Vector3.Lerp(
                _panel.position, targetPos, 1f - Mathf.Exp(-positionLerp * dt));
            _panel.rotation = Quaternion.Slerp(
                _panel.rotation, targetRot, 1f - Mathf.Exp(-rotationLerp * dt));
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            Debug.Log($"[RobotCamOverlay] hb frames:{_beatFrames} " +
                      $"({_beatFrames / heartbeatSeconds:0.0}/s) seq:{_lastSeq} drops:{_drops} " +
                      $"{_tex.width}x{_tex.height}" +
                      (_beatFrames == 0 ? "  (no video — DevVLA streamer enabled + in Play?)" : ""));
            _beatFrames = 0;
        }

        void OnGUI()
        {
            if (!showHud) return;
            string state;
            if (!_haveSeq)
            {
                state = "waiting for video";
            }
            else
            {
                float age = Time.unscaledTime - _lastFrameAt;
                state = $"{_tex.width}x{_tex.height} {_rate}/s"
                        + (_drops > 0 ? $" drop:{_drops}" : "")
                        + (age > 1f ? $"  STALE {age:0.0}s" : "");
            }
            GUI.Label(new Rect(10, 94, 560, 22), $"Robot cam :{listenPort}  {state}");
        }
    }
}
