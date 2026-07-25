// VLA XR teleop — robot ghost overlay driver.
//
// Listens for the joint-target echo the teleop server sends back to this
// machine (handtracking/target_echo.py, UDP :9907, one JSON object per
// datagram):
//
//   {"type":"joint_targets","robot":"h1","seq":N,"t":ms,
//    "names":["left_hip_yaw_joint",...],"targets":[q0,...]}   // radians
//
// and drives a RobotOverlayGhost with them, anchored to the player so you see
// how the robot moves relative to your own movements — the mapping offsets
// ARE the point, so the ghost is 1:1 scale and unsmoothed in joint space
// (only the root anchor is smoothed).
//
// Anchor modes:
//   Superimposed — the ghost stands where YOU stood: feet on the tracking-
//                  space floor, XZ under your chest (body-tracked) or head,
//                  yaw from your measured shoulder line (head yaw fallback).
//                  You look at it from inside; glance down at your arms, or
//                  use InFront for a full view. Backface culling means the
//                  torso shell mostly vanishes from inside.
//                  The anchor is captured ONCE (and on Recenter), because the
//                  robot it mirrors is bolted down: DevVLA's pelvis is
//                  Immovable and its body yaw arrives as a waist JOINT target
//                  in the echo. Re-solving the root from your live shoulder
//                  line each frame added your yaw a SECOND time — the ghost
//                  turned at 2x your rate, and the point cloud parented to
//                  this root (RobotPointCloudOverlay) at 3x, since the VLAD
//                  camera pose is already expressed in robot-base frame.
//                  Set followPlayer to restore the old chase behaviour.
//   InFront      — the ghost is parked inFrontDistance ahead of where you
//                  stood when the mode engaged, turned to face you (a demo
//                  stand, not a mirror: your left arm drives ITS left arm).
//                  Re-anchor via the component context menu ▸ Recenter.
//
// These packets arrive over the SEPARATE echo path — the WS :8766 chunk
// stream to DevVLA is untouched; the ghost shows exactly the targets the
// robot scene is playing.

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class RobotOverlayDriver : MonoBehaviour
    {
        public enum AnchorMode { Superimposed, InFront }

        [Header("Ghost")]
        public RobotOverlayGhost ghost;

        [Header("Network")]
        [Tooltip("UDP port the teleop server echoes joint_targets to " +
                 "(profiles/quest_h1.json echo_port).")]
        public int listenPort = 9907;

        [Header("Anchoring")]
        public AnchorMode mode = AnchorMode.Superimposed;
        [Tooltip("InFront mode: meters ahead of the player at anchor time.")]
        public float inFrontDistance = 1.5f;
        [Tooltip("Head pose source. Auto: OVRCameraRig.centerEyeAnchor, else Camera.main.")]
        public Transform headTransform;
        [Tooltip("Body-tracked chest/shoulders for anchor + yaw. Auto-found; " +
                 "empty = head-based anchoring.")]
        public QuestBodyAnchors bodyAnchors;
        [Tooltip("Floor reference (OVRCameraRig.trackingSpace). Its world Y is " +
                 "the floor the ghost stands on; empty = world Y 0.")]
        public Transform trackingSpace;
        [Tooltip("Superimposed: keep re-solving the ghost root from your LIVE pose " +
                 "(legacy chase). Leave OFF — the robot this mirrors has an immovable " +
                 "base and receives your body yaw as a waist joint target, so tracking " +
                 "you here applies that yaw twice (3x for the ghost-parented point cloud).")]
        public bool followPlayer = false;
        [Tooltip("Root anchor smoothing rates (1/s). Joint angles are NOT smoothed.")]
        public float positionLerp = 8f;
        public float rotationLerp = 6f;
        [Tooltip("While the server plays a take back, park the ghost in front of " +
                 "you instead of on you — you stepped out of the robot, so the " +
                 "robot steps out of you. Restores your mode when playback ends.")]
        public bool detachDuringPlayback = true;

        [Header("Transport")]
        [Tooltip("Sender to acknowledge transport commands to. Auto-found.")]
        public VlaTeleopSender sender;

        [Header("Debug")]
        public bool showHud = true;
        [Tooltip("Seconds between repeated heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;

        [Serializable]
        class TargetsPacket
        {
            public string type;
            public string robot;
            public int seq;
            public double t;
            public string[] names;
            public float[] targets;
            // Raw-episode recorder state (server-side RawDemoRecorder, driven
            // by gestures). Absent in old packets -> false/0 (JsonUtility).
            public bool rec;
            public int rec_frames;
            // Teleop scope the mapper honored (full_body/upper_body/hands_only).
            public string mode;
            // Transport state (handtracking/session.py). Absent in old packets
            // -> a default-constructed block with an empty state string.
            public SessionBlock session;
        }

        /// <summary>The take's transport state, as the server sees it — the
        /// only view the person in VR gets of what the recorder is doing.</summary>
        [Serializable]
        public class SessionBlock
        {
            public string state;      // teleop|recording|paused|scrub|blending|playback
            public string episode;    // episode_000003
            public int frames;
            public float t;           // playhead / recorded seconds
            public float dur;         // take length
            public float cursor;      // t/dur, 0..1
            public int ack;           // last transport command the server ran
            public string msg;        // human-readable status
        }

        UdpClient _udp;
        Thread _thread;
        volatile bool _stop;
        readonly object _lock = new object();
        string _latestJson;
        int _received;

        int _consumed;
        int _lastSeq = -1;
        int _appliedJoints;
        string _lastRobot = "";
        bool _rec;
        int _recFrames;
        string _serverMode = "";
        SessionBlock _session = new SessionBlock { state = "" };
        bool _detached;                 // auto-switched to InFront for playback
        AnchorMode _modeBeforePlayback;
        float _lastPacketAt = -1f;      // unscaledTime of last applied packet
        int _drops;                     // cumulative seq gaps
        int _rateCount; float _rateWindowStart; int _rate;   // pkts/s, 1 s bucket

        // Read-only metrics for HUD panels (VlaTeleopHudPanel).
        public bool HasEcho => _lastSeq >= 0;
        public string Robot => _lastRobot;
        public int AppliedJoints => _appliedJoints;
        public int EchoRate => _rate;
        public int Drops => _drops;
        public bool Recording => _rec;
        public int RecFrames => _recFrames;
        public string ServerMode => _serverMode;
        /// <summary>Transport state from the server (never null once an echo
        /// has arrived; ``state`` is empty for pre-transport servers).</summary>
        public SessionBlock Session => _session;
        public string SessionState => _session != null && _session.state != null
            ? _session.state : "";
        public float EchoAge => _lastPacketAt < 0f
            ? float.PositiveInfinity : Time.unscaledTime - _lastPacketAt;
        bool _inFrontAnchored;
        bool _superAnchored;            // Superimposed root solved and held
        Vector3 _superPos;
        Quaternion _superRot = Quaternion.identity;
        Vector3 _inFrontPos;
        Quaternion _inFrontRot = Quaternion.identity;
        Vector3 _lastForward = Vector3.forward;

        // heartbeat counters (reset each beat)
        float _nextBeat;
        int _beatPackets, _beatBodyAnchored;

        void Start()
        {
            if (ghost == null) ghost = GetComponent<RobotOverlayGhost>();
            if (ghost == null)
            {
                Debug.LogError("[RobotOverlay] No RobotOverlayGhost assigned — " +
                               "run Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost.");
                enabled = false;
                return;
            }
            var rig = FindObjectOfType<OVRCameraRig>();
            if (headTransform == null && rig != null) headTransform = rig.centerEyeAnchor;
            if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;
            if (trackingSpace == null && rig != null) trackingSpace = rig.trackingSpace;
            if (bodyAnchors == null) bodyAnchors = FindObjectOfType<QuestBodyAnchors>();

            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));
                _udp.Client.ReceiveTimeout = 500;
            }
            catch (SocketException e)
            {
                Debug.LogError($"[RobotOverlay] Cannot bind UDP :{listenPort} " +
                               $"({e.SocketErrorCode}) — is another overlay running?");
                enabled = false;
                return;
            }
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "robot-overlay-udp" };
            _thread.Start();
            Debug.Log($"[RobotOverlay] Listening for joint_targets on udp :{listenPort}; " +
                      $"mode={mode}, ghost='{ghost.name}' ({ghost.joints.Length} joints). " +
                      "Server side: teleop_server.py --source quest-xr with profile " +
                      "echo_targets=true (default).");
        }

        void OnDestroy()
        {
            _stop = true;
            try { _udp?.Close(); } catch { }
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
                if (data == null || data.Length == 0) continue;
                string json = Encoding.UTF8.GetString(data);
                lock (_lock)
                {
                    _latestJson = json;
                    _received++;
                }
            }
        }

        void Update()
        {
            string json = null;
            lock (_lock)
            {
                if (_received != _consumed)
                {
                    json = _latestJson;
                    _consumed = _received;
                }
            }
            if (json != null)
            {
                TargetsPacket pkt = null;
                try { pkt = JsonUtility.FromJson<TargetsPacket>(json); }
                catch (Exception) { }
                if (pkt != null && pkt.type == "joint_targets" && pkt.names != null)
                {
                    _appliedJoints = ghost.SetTargets(pkt.names, pkt.targets);
                    if (_lastSeq >= 0 && pkt.seq > _lastSeq + 1)
                        _drops += pkt.seq - _lastSeq - 1;
                    _lastSeq = pkt.seq;
                    _lastRobot = pkt.robot;
                    _rec = pkt.rec;
                    _recFrames = pkt.rec_frames;
                    _serverMode = pkt.mode ?? "";
                    if (pkt.session != null && !string.IsNullOrEmpty(pkt.session.state))
                    {
                        _session = pkt.session;
                        // Tell the sender its transport command landed so it
                        // stops repeating it.
                        if (sender == null) sender = FindObjectOfType<VlaTeleopSender>();
                        if (sender != null) sender.AcknowledgeCommand(_session.ack);
                        UpdatePlaybackDetach();
                    }
                    _lastPacketAt = Time.unscaledTime;
                    _beatPackets++;
                    _rateCount++;
                }
            }
            if (Time.unscaledTime - _rateWindowStart >= 1f)
            {
                _rate = _rateCount;
                _rateCount = 0;
                _rateWindowStart = Time.unscaledTime;
            }
            Heartbeat();
        }

        void LateUpdate() => StepAnchor(Time.deltaTime);

        /// <summary>Move the ghost root toward its anchor pose. LateUpdate calls
        /// this each frame; edit-mode harnesses (VLATeleopAnchorHeadlessTest)
        /// call it directly since LateUpdate never fires outside Play mode.</summary>
        public void StepAnchor(float dt)
        {
            if (ghost == null || headTransform == null) return;
            float floorY = trackingSpace != null ? trackingSpace.position.y : 0f;

            Vector3 targetPos;
            Quaternion targetRot;
            if (mode == AnchorMode.InFront)
            {
                if (!_inFrontAnchored)
                {
                    Vector3 fwd = HorizontalForward();
                    Vector3 at = headTransform.position + fwd * inFrontDistance;
                    _inFrontPos = new Vector3(at.x, floorY + ghost.feetToPelvis, at.z);
                    _inFrontRot = Quaternion.LookRotation(-fwd);   // face the player
                    _inFrontAnchored = true;
                }
                targetPos = _inFrontPos;
                targetRot = _inFrontRot;
            }
            else
            {
                // The ghost stands in for a robot with an IMMOVABLE base, so the
                // root is solved from your pose ONCE and then held; your body yaw
                // reaches the ghost as the waist joint target in the echo. Solving
                // it every frame (followPlayer) adds that yaw a second time.
                if (!_superAnchored || followPlayer)
                {
                    bool body = bodyAnchors != null && bodyAnchors.IsValid;
                    Vector3 refPos = body ? bodyAnchors.Chest : headTransform.position;
                    Vector3 fwd;
                    if (body)
                    {
                        Vector3 right = bodyAnchors.RightShoulder - bodyAnchors.LeftShoulder;
                        right.y = 0f;
                        fwd = right.sqrMagnitude > 1e-6f
                            ? Vector3.Cross(right, Vector3.up).normalized
                            : HorizontalForward();
                        _beatBodyAnchored++;
                    }
                    else
                    {
                        fwd = HorizontalForward();
                    }
                    _superPos = new Vector3(refPos.x, floorY + ghost.feetToPelvis, refPos.z);
                    _superRot = Quaternion.LookRotation(fwd);
                    // Hold from here on unless the body anchor was still warming
                    // up (head fallback) — then re-solve until real tracking lands.
                    _superAnchored = body || bodyAnchors == null;
                }
                targetPos = _superPos;
                targetRot = _superRot;
                _inFrontAnchored = false;          // re-anchor next InFront switch
            }

            ghost.transform.position = Vector3.Lerp(
                ghost.transform.position, targetPos, 1f - Mathf.Exp(-positionLerp * dt));
            ghost.transform.rotation = Quaternion.Slerp(
                ghost.transform.rotation, targetRot, 1f - Mathf.Exp(-rotationLerp * dt));
        }

        Vector3 HorizontalForward()
        {
            Vector3 fwd = headTransform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = _lastForward;   // looking straight up/down
            fwd.Normalize();
            _lastForward = fwd;
            return fwd;
        }

        /// <summary>Re-anchor at the player's current pose (both modes): the
        /// ghost is planted where you stand now, then holds again.</summary>
        [ContextMenu("Recenter")]
        public void Recenter()
        {
            _inFrontAnchored = false;
            _superAnchored = false;
        }

        /// <summary>"Step out of the robot": while the server is playing a take
        /// back, the ghost leaves your body and stands in front of you, so you
        /// watch the demo instead of wearing it. Restores your anchor mode when
        /// playback ends.</summary>
        void UpdatePlaybackDetach()
        {
            if (!detachDuringPlayback) return;
            bool playing = _session.state == "playback";
            if (playing && !_detached)
            {
                _modeBeforePlayback = mode;
                mode = AnchorMode.InFront;
                _inFrontAnchored = false;          // park it where you stand now
                _detached = true;
                Debug.Log("[RobotOverlay] playback — ghost stepped out in front " +
                          $"({_session.episode}, {_session.frames}f).");
            }
            else if (!playing && _detached)
            {
                mode = _modeBeforePlayback;
                _detached = false;
                Debug.Log("[RobotOverlay] playback ended — ghost back on you.");
            }
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            Debug.Log($"[RobotOverlay] hb pkts:{_beatPackets} " +
                      $"({_beatPackets / heartbeatSeconds:0.0}/s) robot:{_lastRobot} " +
                      $"seq:{_lastSeq} joints:{_appliedJoints}/{ghost.joints.Length} " +
                      $"drops:{_drops} mode:{mode} " +
                      $"anchor:{(_superAnchored && !followPlayer ? "held" : _beatBodyAnchored > 0 ? "body" : "head")}" +
                      (_rec ? $" REC:{_recFrames}f" : "") +
                      (_beatPackets == 0 ? "  (no echo — server running with echo_targets on?)" : ""));
            _beatPackets = 0;
            _beatBodyAnchored = 0;
        }

        void OnGUI()
        {
            if (!showHud) return;
            string state;
            if (_lastSeq < 0)
            {
                state = "waiting for joint_targets echo";
            }
            else
            {
                float age = Time.unscaledTime - _lastPacketAt;
                state = $"robot:{_lastRobot} joints:{_appliedJoints} {_rate}/s"
                        + (_drops > 0 ? $" drop:{_drops}" : "")
                        + (_serverMode.Length > 0 ? $" [{_serverMode}]" : "")
                        + (age > 0.5f ? $"  STALE {age:0.0}s" : "");
            }
            GUI.Label(new Rect(10, 34, 760, 22),
                      $"Robot ghost :{listenPort}  {state}  mode:{mode}");

            // Transport indicator — the take's state has to be unmissable; the
            // person in VR has no other way to know whether it is rolling.
            string s = SessionState;
            if (s.Length > 0 && s != "teleop")
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22, fontStyle = FontStyle.Bold
                };
                var prev = GUI.color;
                GUI.color = s == "recording" ? Color.red
                          : s == "playback" ? new Color(0.4f, 0.8f, 1f)
                          : new Color(1f, 0.8f, 0.3f);
                GUI.Label(new Rect(10, 58, 600, 30),
                          $"{Glyph(s)} {s.ToUpperInvariant()}  {_session.frames}f  " +
                          $"{_session.t:0.0}/{_session.dur:0.0}s  {_session.msg}",
                          style);
                GUI.color = prev;
            }
            else if (_rec)   // pre-transport server: plain REC flag only
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22, fontStyle = FontStyle.Bold
                };
                var prev = GUI.color;
                GUI.color = Color.red;
                GUI.Label(new Rect(10, 58, 400, 30), $"● REC {_recFrames}f", style);
                GUI.color = prev;
            }
        }

        /// <summary>Transport glyph per state — readable at a glance in the
        /// headset without reading the word.</summary>
        public static string Glyph(string sessionState)
        {
            switch (sessionState)
            {
                case "recording": return "●";
                case "paused": return "❚❚";
                case "scrub": return "◀◀";
                case "blending": return "▸…";
                case "playback": return "▶";
                default: return "·";
            }
        }
    }
}
