// VLA XR teleop — robot POV point cloud overlay.
//
// Receives VLAD depth frames from DevVLA's RobotDepthStreamer (udp :9909;
// executable spec + reference decoder: handtracking/depth_stream.py) and
// renders them as a torn-mesh point cloud: a static W x H grid whose vertex
// shader (VlaTeleop/RobotPointCloud, in Resources/) samples the depth texture
// and unprojects through the streamed intrinsics. The producer is
// deliberately anonymous — simulated Unity depth today, a DA3 bridge
// (Spark / local / Lambda) inferring depth from the RGB stream later; the
// packets are identical.
//
// Placement:
//   GhostAnchored — parented to the RobotOverlayGhost's root, local pose =
//     the packet's camera-in-robot-base pose, so the robot's perceived world
//     materializes around the ghost, registered to it. (Assumes the ghost
//     root sits at the URDF base link, which is what RobotOverlayBuilder
//     produces and what DevVLA uses as baseFrame.)
//   InFront — a world anchor parked in front of the user at enable time
//     stands in for the robot base (context menu: Recenter In Front).
//   Auto — GhostAnchored when a ghost exists, else InFront.
//
// Color: if the RobotCameraOverlay (:9908 RGB) is running, its latest JPEG
// texture colors the cloud (v1 uses the LATEST frame, not seq-exact pairing —
// at 20 Hz RGB vs 10 Hz depth the mismatch is <= ~100 ms; the packet carries
// rgb_seq if exact pairing is ever needed). Without RGB, a depth colormap.
//
// Enable via Tools > Robot Teleop > Camera Stream > Enable Robot Point Cloud
// Overlay (CameraStreamMenu.cs). QuestTeleop is BUILT-IN RP.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class RobotPointCloudOverlay : MonoBehaviour
    {
        public enum AnchorMode { Auto, GhostAnchored, InFront }

        /// <summary>Points = discrete camera-facing billboard quads (a literal
        /// point cloud). Surface = adjacent pixels connected into a torn mesh
        /// (denser, no gaps, but reads as a solid reconstruction).</summary>
        public enum RenderStyle { Points, Surface }

        [Header("Network")]
        [Tooltip("UDP port DevVLA's RobotDepthStreamer sends VLAD frames to.")]
        public int listenPort = 9909;

        [Header("Placement")]
        public AnchorMode mode = AnchorMode.Auto;
        [Tooltip("Ghost to register the cloud to (Auto/GhostAnchored). Auto-found.")]
        public RobotOverlayGhost ghost;
        [Tooltip("InFront mode: meters in front of the head where the virtual robot base parks.")]
        public float inFrontDistance = 1.5f;
        [Tooltip("Head pose source. Auto: OVRCameraRig.centerEyeAnchor, else Camera.main.")]
        public Transform headTransform;

        [Header("Rendering")]
        [Tooltip("Points = discrete billboard points (literal point cloud). " +
                 "Surface = connected torn mesh (denser, no gaps).")]
        public RenderStyle style = RenderStyle.Points;
        [Tooltip("Points mode: billboard edge length in meters.")]
        public float pointSize = 0.012f;
        [Tooltip("Surface mode: depth-jump fraction of z above which triangles are torn (silhouette edges).")]
        public float tearFraction = 0.06f;
        [Tooltip("Colormap range in meters when no RGB overlay is running.")]
        public float colormapRange = 6f;

        [Header("Debug")]
        public bool showHud = true;
        [Tooltip("Seconds between heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;

        const int HeaderSize = 72;
        const uint NoRgbSeq = 0xFFFFFFFF;

        UdpClient _udp;
        Thread _thread;
        volatile bool _stop;
        readonly object _lock = new object();
        byte[] _latestPacket;
        int _received;
        int _consumed;

        Transform _anchor;          // robot base stand-in (ghost root or parked)
        Transform _cloud;           // mesh transform = camera pose within anchor
        Mesh _mesh;
        Material _mat;
        Texture2D _depthTex;
        byte[] _depthBuf;
        int _w, _h;
        RobotCameraOverlay _rgbOverlay;
        Texture2D _colorOverride;   // set by ApplyPacketDirect (headless render)
        RenderStyle _builtStyle;    // style the current mesh/material were built for
        bool _ownAnchor;

        uint _lastSeq;
        uint _lastRgbSeq;
        bool _haveSeq;
        int _drops;
        float _lastFrameAt = -1f;
        int _rateCount; float _rateWindowStart; int _rate;
        float _nextBeat; int _beatFrames;

        void Start()
        {
            if (headTransform == null)
            {
                var rig = FindObjectOfType<OVRCameraRig>();
                if (rig != null) headTransform = rig.centerEyeAnchor;
            }
            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;
            if (ghost == null) ghost = FindObjectOfType<RobotOverlayGhost>();
            _rgbOverlay = FindObjectOfType<RobotCameraOverlay>();

            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));
                _udp.Client.ReceiveTimeout = 500;
            }
            catch (SocketException e)
            {
                Debug.LogError($"[RobotPointCloud] Cannot bind UDP :{listenPort} " +
                               $"({e.SocketErrorCode}) — is another overlay running?");
                enabled = false;
                return;
            }
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "robot-depth-udp" };
            _thread.Start();

            Debug.Log($"[RobotPointCloud] Listening for VLAD frames on udp :{listenPort}. " +
                      "DevVLA side: VLA Control Panel > Teleop Mode > Enable Robot Depth Stream." +
                      (ghost != null ? " Ghost found — cloud will register to it." : ""));
        }

        void OnDestroy()
        {
            _stop = true;
            try { _udp?.Close(); } catch { }
            if (_cloud != null) Destroy(_cloud.gameObject);
            if (_ownAnchor && _anchor != null) Destroy(_anchor.gameObject);
            if (_mesh != null) Destroy(_mesh);
            if (_mat != null) Destroy(_mat);
            if (_depthTex != null) Destroy(_depthTex);
        }

        void ReceiveLoop()
        {
            IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                byte[] data;
                try { data = _udp.Receive(ref any); }
                catch (SocketException) { continue; }        // timeout — poll _stop
                catch (ObjectDisposedException) { return; }
                if (data == null || data.Length < HeaderSize ||
                    data[0] != 'V' || data[1] != 'L' || data[2] != 'A' || data[3] != 'D')
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

        void ApplyFrame(byte[] p)
        {
            if (!DecodeAndUpload(p, out Vector3 lpos, out Quaternion lrot)) return;
            UpdateAnchor();
            _cloud.localPosition = lpos;
            _cloud.localRotation = lrot;
            if (!_cloud.gameObject.activeSelf) _cloud.gameObject.SetActive(true);

            _lastFrameAt = Time.unscaledTime;
            _rateCount++;
            _beatFrames++;
        }

        /// <summary>Decode a VLAD packet: build/refresh the cloud mesh+material,
        /// upload the depth texture, set intrinsics/color, and hand back the
        /// camera pose (Unity-frame, relative to the robot base) for the caller
        /// to place. Returns false on a malformed packet. Shared by the live
        /// ApplyFrame path and the headless render harness (ApplyPacketDirect).</summary>
        bool DecodeAndUpload(byte[] p, out Vector3 localPos, out Quaternion localRot)
        {
            localPos = Vector3.zero;
            localRot = Quaternion.identity;
            uint seq = BitConverter.ToUInt32(p, 4);
            uint rgbSeq = BitConverter.ToUInt32(p, 8);
            int w = p[12] | p[13] << 8;
            int h = p[14] | p[15] << 8;
            byte encoding = p[17];
            if (encoding != 0 || w <= 1 || h <= 1) return false;   // raw uint16 mm only
            if (p.Length != HeaderSize + w * h * 2) return false;

            if (_haveSeq && seq > _lastSeq + 1) _drops += (int)(seq - _lastSeq - 1);
            _lastSeq = seq;
            _lastRgbSeq = rgbSeq;
            _haveSeq = true;

            if (w != _w || h != _h || style != _builtStyle) RebuildCloud(w, h);

            // Depth rows arrive top-first and are uploaded as-is; the shader
            // indexes the texture in that same order, so no CPU flip.
            Buffer.BlockCopy(p, HeaderSize, _depthBuf, 0, _depthBuf.Length);
            _depthTex.LoadRawTextureData(_depthBuf);
            _depthTex.Apply(false);

            _mat.SetFloat("_Fx", BitConverter.ToSingle(p, 20));
            _mat.SetFloat("_Fy", BitConverter.ToSingle(p, 24));
            _mat.SetFloat("_Cx", BitConverter.ToSingle(p, 28));
            _mat.SetFloat("_Cy", BitConverter.ToSingle(p, 32));
            _mat.SetFloat("_ColorRange", colormapRange);
            if (style == RenderStyle.Points) _mat.SetFloat("_PointSize", pointSize);
            else _mat.SetFloat("_TearFrac", tearFraction);

            Texture2D rgb = _colorOverride != null ? _colorOverride
                          : (_rgbOverlay != null ? _rgbOverlay.LatestTexture : null);
            _mat.SetFloat("_HasColor", rgb != null ? 1f : 0f);
            if (rgb != null) _mat.SetTexture("_ColorTex", rgb);

            // Camera pose in robot base frame, ROS -> Unity
            // (FrameConversions convention: pos (-y, z, x); quat (qy, -qz, -qx, qw)).
            float px = BitConverter.ToSingle(p, 44);
            float py = BitConverter.ToSingle(p, 48);
            float pz = BitConverter.ToSingle(p, 52);
            float qx = BitConverter.ToSingle(p, 56);
            float qy = BitConverter.ToSingle(p, 60);
            float qz = BitConverter.ToSingle(p, 64);
            float qw = BitConverter.ToSingle(p, 68);
            localPos = new Vector3(-py, pz, px);
            localRot = new Quaternion(qy, -qz, -qx, qw);
            return true;
        }

        /// <summary>Headless / edit-mode entry: decode one VLAD packet and place
        /// the resulting cloud under <paramref name="parent"/> at the camera
        /// pose the packet carries, bypassing UDP and head/ghost anchoring. Used
        /// by VLADepthOverlayHeadlessTest to render + verify the VR-side
        /// unprojection without a headset. Returns the cloud transform, or null
        /// on a malformed packet. Start() never runs in edit mode, so no
        /// networking thread is created.</summary>
        public Transform ApplyPacketDirect(byte[] p, Transform parent, Texture2D color = null)
        {
            _colorOverride = color;
            if (!DecodeAndUpload(p, out Vector3 lpos, out Quaternion lrot)) return null;
            _cloud.SetParent(parent, false);
            _cloud.localPosition = lpos;
            _cloud.localRotation = lrot;
            _cloud.gameObject.SetActive(true);
            return _cloud;
        }

        void RebuildCloud(int w, int h)
        {
            _w = w; _h = h;
            _builtStyle = style;
            if (_depthTex != null) DestroyImmediate(_depthTex);
            _depthTex = new Texture2D(w, h, TextureFormat.R16, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _depthBuf = new byte[w * h * 2];

            if (_cloud == null)
            {
                var go = new GameObject("RobotPointCloud");
                _cloud = go.transform;
                go.SetActive(false);
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.AddComponent<MeshFilter>();
            }

            // Shader + material depend on the render style (points vs surface).
            string shaderName = style == RenderStyle.Points
                ? "RobotPointCloudPoints" : "RobotPointCloud";
            var shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[RobotPointCloud] Resources/{shaderName}.shader missing.");
                enabled = false;
                return;
            }
            if (_mat == null || _mat.shader != shader)
            {
                if (_mat != null) DestroyImmediate(_mat);
                _mat = new Material(shader);
                // Single Pass Instanced stereo: keep the INSTANCING_ON variant
                // live so the shader's per-eye matrices resolve in the headset.
                _mat.enableInstancing = true;
                _cloud.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            }
            _mat.SetTexture("_DepthTex", _depthTex);
            _mat.SetFloat("_TexW", w);
            _mat.SetFloat("_TexH", h);

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "RobotPointCloudGrid" };
                _cloud.GetComponent<MeshFilter>().sharedMesh = _mesh;
            }
            _mesh.Clear();
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            if (style == RenderStyle.Points) BuildPointsMesh(w, h);
            else BuildSurfaceMesh(w, h);

            // Vertices move in the shader — keep a fat bound so it never culls.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(200f, 200f, 200f));
            Debug.Log($"[RobotPointCloud] {style} mesh rebuilt: {w}x{h} ({w * h} pts).");
        }

        /// <summary>4 verts/pixel: a camera-facing billboard quad. vertex.xy =
        /// corner in {-0.5,+0.5}, uv = the pixel's normalized image coord.</summary>
        void BuildPointsMesh(int w, int h)
        {
            int n = w * h;
            var verts = new Vector3[n * 4];
            var uvs = new Vector2[n * 4];
            var tris = new int[n * 6];
            var corners = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f),
                                  new Vector3(0.5f, 0.5f),   new Vector3(-0.5f, 0.5f) };
            int t = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    var uv = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                    for (int k = 0; k < 4; k++) { verts[i * 4 + k] = corners[k]; uvs[i * 4 + k] = uv; }
                    tris[t++] = i * 4; tris[t++] = i * 4 + 1; tris[t++] = i * 4 + 2;
                    tris[t++] = i * 4; tris[t++] = i * 4 + 2; tris[t++] = i * 4 + 3;
                }
            }
            _mesh.vertices = verts;
            _mesh.uv = uvs;
            _mesh.triangles = tris;
        }

        /// <summary>1 vert/pixel connected into a torn grid surface. Positions
        /// come from the vertex shader; uv = normalized image coord (y down).</summary>
        void BuildSurfaceMesh(int w, int h)
        {
            var verts = new Vector3[w * h];
            var uvs = new Vector2[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    uvs[y * w + x] = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
            var tris = new int[(w - 1) * (h - 1) * 6];
            int t = 0;
            for (int y = 0; y < h - 1; y++)
            {
                for (int x = 0; x < w - 1; x++)
                {
                    int i = y * w + x;
                    tris[t++] = i; tris[t++] = i + 1; tris[t++] = i + w;
                    tris[t++] = i + 1; tris[t++] = i + w + 1; tris[t++] = i + w;
                }
            }
            _mesh.vertices = verts;
            _mesh.uv = uvs;
            _mesh.triangles = tris;
        }

        void UpdateAnchor()
        {
            bool useGhost = mode == AnchorMode.GhostAnchored ||
                            (mode == AnchorMode.Auto && ghost != null);
            if (useGhost && ghost != null)
            {
                if (_anchor != ghost.transform)
                {
                    if (_ownAnchor && _anchor != null) Destroy(_anchor.gameObject);
                    _ownAnchor = false;
                    _anchor = ghost.transform;
                    _cloud.SetParent(_anchor, false);
                }
                return;
            }
            if (_anchor == null || !_ownAnchor)
            {
                var go = new GameObject("RobotPointCloudAnchor");
                _anchor = go.transform;
                _ownAnchor = true;
                PlaceAnchorInFront();
                _cloud.SetParent(_anchor, false);
            }
        }

        [ContextMenu("Recenter In Front")]
        public void PlaceAnchorInFront()
        {
            if (_anchor == null || !_ownAnchor || headTransform == null) return;
            Vector3 fwd = headTransform.forward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            _anchor.position = headTransform.position + fwd * inFrontDistance
                               - Vector3.up * 0.4f;
            // Virtual robot base faces the user, like the ghost's InFront mode.
            _anchor.rotation = Quaternion.LookRotation(-fwd);
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            Debug.Log($"[RobotPointCloud] hb frames:{_beatFrames} " +
                      $"({_beatFrames / heartbeatSeconds:0.0}/s) seq:{_lastSeq} drops:{_drops} " +
                      $"{_w}x{_h}" +
                      (_beatFrames == 0 ? "  (no depth — DevVLA depth stream enabled + in Play?)" : ""));
            _beatFrames = 0;
        }

        void OnGUI()
        {
            if (!showHud) return;
            string state;
            if (!_haveSeq)
            {
                state = "waiting for depth";
            }
            else
            {
                float age = Time.unscaledTime - _lastFrameAt;
                string color = _rgbOverlay != null && _rgbOverlay.LatestTexture != null
                    ? (_lastRgbSeq == NoRgbSeq ? "rgb:unpaired" : $"rgb:{_lastRgbSeq}")
                    : "colormap";
                state = $"{_w}x{_h} {_rate}/s {color}"
                        + (_drops > 0 ? $" drop:{_drops}" : "")
                        + (age > 1f ? $"  STALE {age:0.0}s" : "");
            }
            GUI.Label(new Rect(10, 116, 560, 22), $"Robot cloud :{listenPort}  {state}");
        }
    }
}
