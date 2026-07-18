// ReachyBrain XR bridge — Quest port of VitureUnity/Assets/Scripts/ReachyBridgeSender.cs.
//
// Streams XR interaction state to the ReachyBrain XR bridge server
// (`python -m integrations.viture.server` on the Mac, TCP NDJSON :9878) so a
// Reachy Mini can follow the user, follow their hand, or look where they point.
// The wire protocol is byte-for-byte identical to the Viture sender, so the Mac
// server and the on-robot app need NO changes for the follow/raycast/gaze path.
//
// Differences from the Viture sender (same protocol, different sources):
//   * Head pose comes from the OVR center-eye anchor (inside-out tracking)
//     instead of the Viture Carina VIO camera.
//   * Hands/pointer come from QuestPointerProvider (native OVRHand, metric)
//     instead of MediaPipe landmarks projected at a fake 0.6 m depth.
//   * There is no keyboard on Quest — modes/calibration are triggered by
//     ReachyBridgeControls (Touch buttons) and the ReachyBridgeHud panel, which
//     call the public SendMode/SendCalibrate/SendMapCapture methods here.
//   * The Quest is a remote device, so `host` is the Mac's LAN IP (not
//     127.0.0.1). It is overridable at runtime via PlayerPrefs so you don't
//     have to rebuild when the Mac's IP changes.
//
// Sends at sendHz:
//   {"type":"xr_state","ts":..,"head":{"p":[..],"fwd":[..],"up":[..]},
//    "hands":[{"hand":"Left","visible":true,"palm":[..],"ray_o":[..],
//              "ray_d":[..],"pinch":false}, ...]}
// All vectors are Unity world space (left-handed, Y-up, meters).

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ReachyBridge
{
    public class ReachyBridgeSender : MonoBehaviour
    {
        [Header("Server (integrations/viture/server.py on the Mac)")]
        [Tooltip("Mac LAN IP running the XR bridge server. Overridable at runtime via " +
                 "PlayerPrefs key 'reachy_bridge_host' (see SetHost).")]
        public string host = "192.168.0.217";
        public int port = 9878;
        public bool autoConnect = true;

        [Header("Sources (auto-found if left empty)")]
        public OVRCameraRig rig;
        public QuestPointerProvider pointer;

        [Header("Streaming")]
        [Range(5f, 60f)] public float sendHz = 20f;

        const string HostPrefKey = "reachy_bridge_host";
        static readonly string[] Hands = { "Left", "Right" };

        // --- networking (background threads; main thread only enqueues) ---
        TcpClient _client;
        NetworkStream _stream;
        Thread _writerThread;
        Thread _readerThread;
        volatile bool _running;
        volatile bool _connected;
        readonly ConcurrentQueue<string> _sendQueue = new ConcurrentQueue<string>();
        readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);

        float _sendAccum;
        readonly StringBuilder _sb = new StringBuilder(1024);
        Transform _head;

        public bool IsConnected => _connected;
        public string Host => host;

        // --- last status from the server (for the HUD) ---
        volatile string _statusLine = "";
        public string HudMode { get; private set; } = "idle";
        public string HudMessage { get; private set; } = "not connected";
        public bool HudCalibrated { get; private set; }
        public bool HudRobotConnected { get; private set; }
        public bool HudMapCalibrated { get; private set; }
        public bool HudRobotRelocBusy { get; private set; }
        public bool HudMapCapturing { get; private set; }
        public string HudRobotAnchorSource { get; private set; } = "none";
        public int HudCaptures { get; private set; }

        // --- scene-anchoring transforms from the server (map frame = dimos scan) ---
        volatile string _transformsLine = "";
        volatile string _robotHeadLine = "";
        readonly object _xformLock = new object();
        bool _hasMapFromUnity, _hasMapFromRobot, _hasRobotHeadMap;
        Matrix4x4 _mapFromUnity, _mapFromRobot, _robotHeadMap;
        string _mapSession = "";
        float _relocFitness;
        string _relocMethod = "";
        string _anchorSource = "";

        public string MapSession { get { lock (_xformLock) return _mapSession; } }
        public float RelocFitness { get { lock (_xformLock) return _relocFitness; } }
        public string AnchorSource { get { lock (_xformLock) return _anchorSource; } }

        public bool TryGetMapFromUnity(out Matrix4x4 m)
        { lock (_xformLock) { m = _mapFromUnity; return _hasMapFromUnity; } }
        public bool TryGetMapFromRobot(out Matrix4x4 m)
        { lock (_xformLock) { m = _mapFromRobot; return _hasMapFromRobot; } }
        public bool TryGetRobotHeadMap(out Matrix4x4 m)
        { lock (_xformLock) { m = _robotHeadMap; return _hasRobotHeadMap; } }

        // ------------------------------------------------------------------ //
        // Lifecycle                                                           //
        // ------------------------------------------------------------------ //

        void Start()
        {
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            if (pointer == null) pointer = FindObjectOfType<QuestPointerProvider>();
            _head = rig != null ? rig.centerEyeAnchor : null;
            if (_head == null && Camera.main != null) _head = Camera.main.transform;

            var saved = PlayerPrefs.GetString(HostPrefKey, "");
            if (!string.IsNullOrEmpty(saved)) host = saved;

            if (autoConnect) Connect();
        }

        void OnDestroy() => Disconnect();
        void OnApplicationQuit() => Disconnect();

        void OnApplicationPause(bool paused)
        {
            // Dropping the socket on background/resume avoids a half-open TCP
            // connection when the headset sleeps.
            if (paused) Disconnect();
            else if (autoConnect) Connect();
        }

        /// <summary>Point the sender at a new Mac IP and reconnect. Persists so a
        /// rebuild isn't needed next launch (call from an on-device settings UI).</summary>
        public void SetHost(string newHost)
        {
            if (string.IsNullOrWhiteSpace(newHost)) return;
            host = newHost.Trim();
            PlayerPrefs.SetString(HostPrefKey, host);
            PlayerPrefs.Save();
            Disconnect();
            Connect();
        }

        public void Connect()
        {
            if (_running) return;
            _running = true;
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "ReachyBridgeWriter" };
            _writerThread.Start();
        }

        public void Disconnect()
        {
            _running = false;
            _sendSignal.Set();
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _connected = false;
        }

        void WriterLoop()
        {
            while (_running)
            {
                try
                {
                    _client = new TcpClient { NoDelay = true };
                    _client.Connect(host, port);
                    _stream = _client.GetStream();
                    _connected = true;
                    Debug.Log($"[ReachyBridge] connected to {host}:{port}");
                    EnqueueReliable("{\"type\":\"hello\",\"role\":\"xr\",\"name\":\"quest_passthrough\"}");

                    _readerThread = new Thread(() => ReaderLoop(_stream)) { IsBackground = true, Name = "ReachyBridgeReader" };
                    _readerThread.Start();

                    while (_running && _client.Connected)
                    {
                        _sendSignal.WaitOne(200);
                        while (_sendQueue.TryDequeue(out var line))
                        {
                            var bytes = Encoding.UTF8.GetBytes(line + "\n");
                            _stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (_running) Debug.LogWarning($"[ReachyBridge] connection lost: {e.Message} — retrying in 2s");
                }
                finally
                {
                    _connected = false;
                    try { _stream?.Close(); } catch { }
                    try { _client?.Close(); } catch { }
                }
                if (_running) Thread.Sleep(2000);
            }
        }

        void ReaderLoop(NetworkStream stream)
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (_running)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    if (line.Contains("\"xr_status\"")) _statusLine = line;
                    else if (line.Contains("\"xr_transforms\"")) _transformsLine = line;
                    else if (line.Contains("\"xr_robot_head\"")) _robotHeadLine = line;
                }
            }
            catch { /* socket closed */ }
        }

        void EnqueueReliable(string line)
        {
            _sendQueue.Enqueue(line);
            _sendSignal.Set();
        }

        void EnqueueState(string line)
        {
            // Never let a stalled socket back up frames — drop states instead.
            if (_sendQueue.Count > 8) return;
            _sendQueue.Enqueue(line);
            _sendSignal.Set();
        }

        // ------------------------------------------------------------------ //
        // Per-frame: stream state + drain server replies                      //
        // ------------------------------------------------------------------ //

        void Update()
        {
            _sendAccum += Time.deltaTime;
            float period = 1f / Mathf.Max(sendHz, 1f);
            if (_sendAccum >= period && _connected)
            {
                _sendAccum = 0f;
                EnqueueState(BuildStateJson());
            }

            if (!string.IsNullOrEmpty(_statusLine)) { ParseStatus(_statusLine); _statusLine = ""; }
            if (!string.IsNullOrEmpty(_transformsLine)) { ParseTransforms(_transformsLine); _transformsLine = ""; }
            if (!string.IsNullOrEmpty(_robotHeadLine)) { ParseRobotHead(_robotHeadLine); _robotHeadLine = ""; }
        }

        // ------------------------------------------------------------------ //
        // Control messages (called by ReachyBridgeControls / ReachyBridgeHud)  //
        // ------------------------------------------------------------------ //

        public static readonly string[] Modes =
            { "idle", "follow_user", "follow_hand", "raycast", "calibrate" };

        string _lastSentMode = "idle";

        public void SendMode(string mode)
        {
            _lastSentMode = mode;
            EnqueueReliable($"{{\"type\":\"xr_mode\",\"mode\":\"{mode}\"}}");
            Debug.Log($"[ReachyBridge] mode -> {mode}");
        }

        /// <summary>Cycle idle -> follow_user -> follow_hand -> raycast -> calibrate -> idle.
        /// Bases the step on the last mode we sent (not the server round-trip) so a
        /// fast double-press advances two steps instead of re-sending the same one.</summary>
        public void CycleMode()
        {
            int i = Array.IndexOf(Modes, _lastSentMode);
            SendMode(Modes[(i + 1 + Modes.Length) % Modes.Length]);
        }

        public void SendCalibrate(string action)
        {
            EnqueueReliable($"{{\"type\":\"xr_calibrate\",\"action\":\"{action}\"}}");
            Debug.Log($"[ReachyBridge] calibrate: {action}");
        }

        /// <summary>Start/stop/cancel a passthrough walk-capture. The server pulls
        /// frames from the PassthroughVitcPublisher (:9901) on this headset.</summary>
        public void SendMapCapture(string action)
        {
            EnqueueReliable($"{{\"type\":\"xr_map_capture\",\"action\":\"{action}\"}}");
        }

        // ------------------------------------------------------------------ //
        // State serialization                                                 //
        // ------------------------------------------------------------------ //

        static void AppendVec(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            sb.Append(v.x.ToString("F4", CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(v.y.ToString("F4", CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(v.z.ToString("F4", CultureInfo.InvariantCulture));
            sb.Append(']');
        }

        string BuildStateJson()
        {
            var sb = _sb;
            sb.Length = 0;
            sb.Append("{\"type\":\"xr_state\",\"ts\":");
            sb.Append(Time.unscaledTimeAsDouble.ToString("F3", CultureInfo.InvariantCulture));

            // Head = the center-eye anchor pose (inside-out tracking).
            var t = _head != null ? _head : transform;
            sb.Append(",\"head\":{\"p\":");
            AppendVec(sb, t.position);
            sb.Append(",\"fwd\":");
            AppendVec(sb, t.forward);
            sb.Append(",\"up\":");
            AppendVec(sb, t.up);
            sb.Append("},\"hands\":[");

            for (int i = 0; i < Hands.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendHand(sb, i);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        void AppendHand(StringBuilder sb, int handIndex)
        {
            HandPointer hp = default;
            bool visible = pointer != null && pointer.TryGetPointer(handIndex, out hp) && hp.visible;
            if (!visible)
            {
                sb.Append($"{{\"hand\":\"{Hands[handIndex]}\",\"visible\":false}}");
                return;
            }
            sb.Append($"{{\"hand\":\"{Hands[handIndex]}\",\"visible\":true,\"palm\":");
            AppendVec(sb, hp.palm);
            sb.Append(",\"ray_o\":");
            AppendVec(sb, hp.rayOrigin);
            sb.Append(",\"ray_d\":");
            AppendVec(sb, hp.rayDir);
            sb.Append(",\"pinch\":");
            sb.Append(hp.pinch ? "true" : "false");
            sb.Append('}');
        }

        // ------------------------------------------------------------------ //
        // Server replies (parsed on the main thread)                          //
        // ------------------------------------------------------------------ //

        void ParseStatus(string json)
        {
            HudMode = ExtractString(json, "mode") ?? HudMode;
            HudMessage = ExtractString(json, "message") ?? HudMessage;
            HudCalibrated = ExtractBool(json, "calibrated");
            HudRobotConnected = ExtractBool(json, "robot_connected");
            HudMapCalibrated = ExtractBool(json, "map_calibrated");
            HudCaptures = (int)ExtractNumber(json, "captures", HudCaptures);
            HudRobotAnchorSource = ExtractString(json, "robot_anchor_source") ?? HudRobotAnchorSource;
            HudRobotRelocBusy = ExtractBool(json, "robot_reloc_busy");
            HudMapCapturing = ExtractNumber(json, "map_capture_frames", 0) > 0;
        }

        void ParseTransforms(string json)
        {
            lock (_xformLock)
            {
                _mapSession = ExtractString(json, "map_session") ?? _mapSession;
                _relocMethod = ExtractString(json, "reloc_method") ?? _relocMethod;
                _relocFitness = (float)ExtractNumber(json, "reloc_fitness", _relocFitness);
                if (TryExtractMatrix(json, "map_from_unity", out var mu))
                {
                    _mapFromUnity = mu; _hasMapFromUnity = true;
                    _anchorSource = ExtractString(json, "map_from_unity_source") ?? _anchorSource;
                }
                if (TryExtractMatrix(json, "map_from_robot", out var mr)) { _mapFromRobot = mr; _hasMapFromRobot = true; }
                if (TryExtractMatrix(json, "robot_head_map", out var rh)) { _robotHeadMap = rh; _hasRobotHeadMap = true; }
            }
        }

        void ParseRobotHead(string json)
        {
            if (TryExtractMatrix(json, "robot_head_map", out var rh))
                lock (_xformLock) { _robotHeadMap = rh; _hasRobotHeadMap = true; }
        }

        /// <summary>Parse "key":[16 floats, row-major] into a Matrix4x4.</summary>
        static bool TryExtractMatrix(string json, string key, out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            int i = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (i < 0) return false;
            i = json.IndexOf('[', i + key.Length + 3);
            if (i < 0) return false;
            int end = json.IndexOf(']', i);
            if (end < 0) return false;
            var parts = json.Substring(i + 1, end - i - 1).Split(',');
            if (parts.Length != 16) return false;
            var vals = new float[16];
            for (int k = 0; k < 16; k++)
                if (!float.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[k]))
                    return false;
            for (int r = 0; r < 4; r++)
                m.SetRow(r, new Vector4(vals[r * 4 + 0], vals[r * 4 + 1], vals[r * 4 + 2], vals[r * 4 + 3]));
            return true;
        }

        static string ExtractString(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length + 3;
            while (i < json.Length && json[i] == ' ') i++;
            if (i >= json.Length || json[i] != '"') return null;
            int end = json.IndexOf('"', i + 1);
            return end < 0 ? null : json.Substring(i + 1, end - i - 1);
        }

        static bool ExtractBool(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (i < 0) return false;
            return string.CompareOrdinal(json, i + key.Length + 3, "true", 0, 4) == 0;
        }

        static double ExtractNumber(string json, string key, double fallback)
        {
            int i = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
            if (i < 0) return fallback;
            i += key.Length + 3;
            int end = i;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.')) end++;
            return double.TryParse(json.Substring(i, end - i), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
