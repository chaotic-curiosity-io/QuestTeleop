// VLA XR teleop — Quest port of VitureUnity/Assets/Scripts/VlaTeleopSender.cs.
//
// Streams the XR teleop state — headset pose + hand landmarks — as UDP JSON to
// the openvla-unity-sim2real robot-teleop pipeline (Pipeline 3: robot IK from a
// hand EE target). This is the SECOND bridge in the Viture app (the first,
// ReachyBridge/, is already ported here); it drives real robot joints
// (fingers + arm IK + head) rather than the ReachyBrain follow/gaze modes.
//
//   head pose:  OVRCameraRig.centerEyeAnchor (inside-out tracking)
//   hands:      native OVRSkeleton, remapped to MediaPipe's 21-landmark order by
//               QuestHandLandmarks (metric world joints), plus each wrist's true
//               metric world position — no heuristic depth (Viture's big wart).
//
// One JSON object per datagram at sendHz, sent to every endpoint:
//   127.0.0.1:9905  handtracking/teleop_server.py --source viture-xr
//                   (fingers + arm IK + head joints -> WS :8766 -> GrootBridge)
//   127.0.0.1:9906  DevVLA TeleopHeadCamera (drives the robot scene's EgoCam)
//
// The Python side is UNCHANGED from the Viture path — it's the same "xr_pose"
// packet. Because Quest reports true handedness (OVRSkeleton.HandLeft/HandRight),
// left hand -> payload.left maps straight to the robot's left arm: run the teleop
// server WITHOUT --swap-hands (unlike the egocentric-MediaPipe Viture path).
//
// Everything is converted Unity -> ROS robot frame (x fwd, y left, z up,
// right-handed; ROS-TCP-Connector mapping) before sending:
//   pos  (x,y,z)ros  = ( u.z, -u.x,  u.y)
//   quat (x,y,z,w)ros = (-u.z,  u.x, -u.y, u.w)
//
// Packet schema (superset of the Viture sender's; extra fields are ignored by
// the old parsers, so the Viture path is untouched):
//   {"type":"xr_pose","seq":N,"t":ms,"source":"quest",
//    "head_pos":[3],"head_quat":[4],
//    "left":{"visible":b,"conf":0..1,"wrist":[3],"wrist_quat":[4],"landmarks":[63]},
//    "right":{...},
//    "body":{"valid":b,"chest":[3],"l_shoulder":[3],"r_shoulder":[3]},
//    "cmd":{"id":N,"name":"rec_start","arg":0},
//    "scrub":{"session":N,"active":b,"pos":0..1}}
//
// "conf" is OVRHand confidence (1 high, 0.5 low — low-confidence frames HOLD
// on the Python side instead of chasing tracker noise). "body" carries the
// body-tracked chest/shoulder anchors (QuestBodyAnchors) so the server can IK
// from your MEASURED shoulders; when invalid the server falls back to the
// virtual head-offset anchor. Run the server with --source quest-xr.
//
// "cmd" and "scrub" are the in-VR TRANSPORT: TeleopGestureCommands.cs turns
// hand poses into record/pause/rewind/playback commands and this sender
// carries them. A command REPEATS in every packet until the server echoes its
// id back as session.ack (RobotOverlayDriver calls AcknowledgeCommand), so a
// dropped datagram can't swallow a "stop recording".

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace VlaTeleop
{
    public class VlaTeleopSender : MonoBehaviour
    {
        /// <summary>Teleop scope — which joint groups the SERVER drives from
        /// this stream (masked server-side per frame; masked groups hold their
        /// last targets). Rides in every packet as "mode"; the server echoes
        /// the honored mode back in joint_targets for the ghost HUD.</summary>
        public enum TeleopMode
        {
            FullBody,    // fingers + arms + torso + head + legs
            UpperBody,   // fingers + arms + torso + head
            HandsOnly,   // finger curls only
        }

        [Header("Sources (auto-found if left empty)")]
        [Tooltip("The OVR rig; head pose is read from its centerEyeAnchor. Falls back to Camera.main.")]
        public OVRCameraRig rig;
        [Tooltip("Explicit head transform override. Defaults to rig.centerEyeAnchor / Camera.main.")]
        public Transform headTransform;
        public OVRHand leftHand;
        public OVRHand rightHand;
        public OVRSkeleton leftSkeleton;
        public OVRSkeleton rightSkeleton;

        [Header("Output")]
        [Tooltip("host:port UDP endpoints. Defaults: teleop_server.py --source viture-xr (:9905) and DevVLA TeleopHeadCamera (:9906).")]
        public string[] endpoints = { "127.0.0.1:9905", "127.0.0.1:9906" };
        [Range(5f, 90f)] public float sendHz = 30f;

        [Header("Teleop mode")]
        [Tooltip("Which joint groups the server drives. Cycle in VR with a " +
                 "double MIDDLE-finger pinch on either hand, or via the " +
                 "component context menu. (Recording is a separate control — " +
                 "see TeleopGestureCommands.)")]
        public TeleopMode mode = TeleopMode.FullBody;

        [Header("Transport (TeleopGestureCommands)")]
        [Tooltip("Seconds to keep repeating an unacknowledged transport command " +
                 "before giving up (no echo listener / server down).")]
        public float commandRetrySeconds = 1.5f;

        [Header("Debug")]
        public bool showHud = true;

        [Header("Body tracking (optional)")]
        [Tooltip("Body-tracked chest/shoulder anchors. Auto-found in the scene; " +
                 "empty = the packet's body block stays invalid (server falls " +
                 "back to the virtual chest anchor).")]
        public QuestBodyAnchors bodyAnchors;

        [Header("Logging")]
        [Tooltip("Seconds between repeated heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;

        // ---- packet DTOs (JsonUtility) -------------------------------------- //
        [Serializable]
        public class HandPayload
        {
            public bool visible;
            public float conf;                  // 1 high, 0.5 low, 0 untracked
            public float[] wrist = Empty;       // ROS frame, meters
            public float[] wrist_quat = Empty;  // ROS frame, xyzw
            public float[] landmarks = Empty;   // 21*(x,y,z), ROS frame, metric
            static readonly float[] Empty = new float[0];
        }

        [Serializable]
        public class BodyPayload
        {
            public bool valid;
            public float[] chest = new float[3];       // ROS frame, meters
            public float[] l_shoulder = new float[3];
            public float[] r_shoulder = new float[3];
            // FullBody (generative) legs — hip/knee/ankle/toe per side.
            public bool legs_valid;
            public float[] l_hip = new float[3];
            public float[] r_hip = new float[3];
            public float[] l_knee = new float[3];
            public float[] r_knee = new float[3];
            public float[] l_ankle = new float[3];
            public float[] r_ankle = new float[3];
            public float[] l_toe = new float[3];
            public float[] r_toe = new float[3];
        }

        /// <summary>Transport command (record / pause / play / nudge). Repeated
        /// in every packet until the server acks the id — UDP drops, and a lost
        /// "stop recording" would be a lost take. id 0 = nothing pending.</summary>
        [Serializable]
        public class CmdPayload
        {
            public int id;
            public string name = "";
            public float arg;
        }

        /// <summary>Continuous timeline scrub. ``session`` increments per drag,
        /// so the server can tell a fresh drag from a repeat of the release
        /// that commits the previous one. session -1 = never scrubbed.</summary>
        [Serializable]
        public class ScrubPayload
        {
            public int session = -1;
            public bool active;
            public float pos;
        }

        [Serializable]
        public class XrPosePacket
        {
            public string type = "xr_pose";
            public string source = "quest";
            public int seq;
            public double t;                    // ms since startup
            public string mode = "full_body";   // teleop scope (server masks)
            public float[] head_pos = new float[3];
            public float[] head_quat = new float[4];
            public HandPayload left = new HandPayload();
            public HandPayload right = new HandPayload();
            public BodyPayload body = new BodyPayload();
            public CmdPayload cmd = new CmdPayload();
            public ScrubPayload scrub = new ScrubPayload();
        }

        static string ModeString(TeleopMode m) => m switch
        {
            TeleopMode.UpperBody => "upper_body",
            TeleopMode.HandsOnly => "hands_only",
            _ => "full_body",
        };

        Socket _socket;
        readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        readonly XrPosePacket _packet = new XrPosePacket();

        /// <summary>The most recent packet built + sent (ROS robot frame). Read-only
        /// handle for overlay/gizmo tools (VlaTeleopGizmos) so they draw EXACTLY what
        /// went on the wire, not a re-derivation.</summary>
        public XrPosePacket LastPacket => _packet;

        /// <summary>Read-only metrics for HUD panels (VlaTeleopHudPanel).</summary>
        public int SentCount => _sent;
        public string LastSendError => _lastError;

        readonly Vector3[] _world21 = new Vector3[QuestHandLandmarks.Count];
        float _nextSend;
        int _sent;
        string _lastError;

        // heartbeat counters (reset each beat)
        float _nextBeat;
        int _beatSent, _beatLeft, _beatRight, _beatBody, _beatLegs, _beatLowConf, _beatSendErr;

        void Start()
        {
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            if (headTransform == null && rig != null) headTransform = rig.centerEyeAnchor;
            if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;
            if (bodyAnchors == null) bodyAnchors = FindObjectOfType<QuestBodyAnchors>();
            AutoFindHands();

            if (headTransform == null)
            {
                Debug.LogError("[VlaTeleop] No head transform (no OVRCameraRig.centerEyeAnchor and no Camera.main) — nothing to stream.");
                enabled = false;
                return;
            }

            foreach (var ep in endpoints)
            {
                if (string.IsNullOrWhiteSpace(ep)) continue;
                int c = ep.LastIndexOf(':');
                if (c <= 0 || !int.TryParse(ep.Substring(c + 1), out int port) ||
                    !IPAddress.TryParse(ep.Substring(0, c), out IPAddress addr))
                {
                    Debug.LogError($"[VlaTeleop] Bad endpoint '{ep}' (want host:port).");
                    continue;
                }
                _targets.Add(new IPEndPoint(addr, port));
            }
            if (_targets.Count == 0)
            {
                Debug.LogError("[VlaTeleop] No valid UDP endpoints.");
                enabled = false;
                return;
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Blocking = false;
            Debug.Log($"[VlaTeleop] Streaming xr_pose to {string.Join(", ", endpoints)} at {sendHz:0} Hz "
                      + ((leftHand != null || rightHand != null) ? "(head + native hands)."
                                                                  : "(head only — no OVRHand in scene)."));
        }

        void AutoFindHands()
        {
            if (leftHand != null && rightHand != null) return;
            foreach (var h in FindObjectsOfType<OVRHand>(includeInactive: true))
            {
                var skel = h.GetComponent<OVRSkeleton>();
                var type = skel != null ? skel.GetSkeletonType() : OVRSkeleton.SkeletonType.None;
                bool typed = type == OVRSkeleton.SkeletonType.HandLeft
                             || type == OVRSkeleton.SkeletonType.HandRight
                             || type == OVRSkeleton.SkeletonType.XRHandLeft
                             || type == OVRSkeleton.SkeletonType.XRHandRight;
                bool isLeft = type == OVRSkeleton.SkeletonType.HandLeft
                              || type == OVRSkeleton.SkeletonType.XRHandLeft
                              || (!typed && h.name.ToLowerInvariant().Contains("left"));
                if (isLeft && leftHand == null) { leftHand = h; leftSkeleton = skel; }
                else if (!isLeft && rightHand == null) { rightHand = h; rightSkeleton = skel; }
            }
        }

        void OnDestroy()
        {
            try { _socket?.Close(); } catch { }
        }

        // Mode-cycle gesture state (double MIDDLE pinch, either hand).
        readonly float[] _lastMiddleDown = { -10f, -10f };
        readonly bool[] _middleWas = new bool[2];
        float _modeDebounce;

        void Update()
        {
            DetectModeGesture();

            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / Mathf.Max(5f, sendHz);

            _packet.seq = ++_sent;
            _packet.t = Time.unscaledTimeAsDouble * 1000.0;
            _packet.mode = ModeString(mode);
            WriteRosPose(headTransform.position, headTransform.rotation,
                         _packet.head_pos, _packet.head_quat);

            FillHand(_packet.left, leftHand, leftSkeleton);
            FillHand(_packet.right, rightHand, rightSkeleton);
            FillBody(_packet.body);
            ExpireCommand();      // AFTER the ack check, BEFORE serializing

            byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(_packet));
            foreach (var ep in _targets)
            {
                try { _socket.SendTo(data, ep); _lastError = null; }
                catch (SocketException e)
                {
                    _lastError = e.SocketErrorCode.ToString();
                    _beatSendErr++;
                }
            }

            _beatSent++;
            if (_packet.left.visible) _beatLeft++;
            if (_packet.right.visible) _beatRight++;
            if (_packet.body.valid) _beatBody++;
            if (_packet.body.legs_valid) _beatLegs++;
            if ((_packet.left.visible && _packet.left.conf < 0.9f)
                || (_packet.right.visible && _packet.right.conf < 0.9f)) _beatLowConf++;
            Heartbeat();
        }

        /// <summary>Double MIDDLE-finger pinch (shut → open → shut inside
        /// 1.5 s) on either hand cycles the teleop mode. The index finger is
        /// left alone: an index pinch-and-hold is the transport's timeline
        /// scrub, and a full-fist double pinch is the server's fallback
        /// record toggle.</summary>
        void DetectModeGesture()
        {
            if (Time.unscaledTime < _modeDebounce) return;
            var hands = new[] { leftHand, rightHand };
            for (int i = 0; i < 2; i++)
            {
                var h = hands[i];
                if (h == null || !h.IsTracked) { _middleWas[i] = false; continue; }
                bool now = h.GetFingerIsPinching(OVRHand.HandFinger.Middle);
                if (now && !_middleWas[i])                       // rising edge
                {
                    if (Time.unscaledTime - _lastMiddleDown[i] < 1.5f)
                    {
                        CycleMode();
                        _modeDebounce = Time.unscaledTime + 1f;
                        _lastMiddleDown[i] = -10f;
                    }
                    else
                    {
                        _lastMiddleDown[i] = Time.unscaledTime;
                    }
                }
                _middleWas[i] = now;
            }
        }

        [ContextMenu("Cycle Mode")]
        public void CycleMode()
        {
            mode = (TeleopMode)(((int)mode + 1) % 3);
            Debug.Log($"[VlaTeleop] teleop mode -> {mode}");
        }

        // ---- transport commands (TeleopGestureCommands drives these) -------- //

        int _cmdId;
        float _cmdSentAt = -1f;
        /// <summary>Last command id the server confirmed (echo `session.ack`).</summary>
        public int AckedCommandId { get; private set; }
        /// <summary>Command still awaiting an ack (empty when idle) — HUD.</summary>
        public string PendingCommand =>
            _packet.cmd.id > AckedCommandId ? _packet.cmd.name : "";

        /// <summary>Queue a transport command for handtracking/session.py. It
        /// rides in every packet until acked or <see cref="commandRetrySeconds"/>
        /// elapses; the server keys de-duplication on the id, so repeats are
        /// free.</summary>
        public void SendCommand(string wireName, float arg = 0f)
        {
            if (string.IsNullOrEmpty(wireName)) return;
            _packet.cmd.id = ++_cmdId;
            _packet.cmd.name = wireName;
            _packet.cmd.arg = arg;
            _cmdSentAt = Time.unscaledTime;
        }

        /// <summary>Called by RobotOverlayDriver when the echo reports the
        /// server's last executed command id.</summary>
        public void AcknowledgeCommand(int ackId)
        {
            if (ackId > AckedCommandId) AckedCommandId = ackId;
        }

        /// <summary>Timeline scrub state (continuous; latest packet wins).</summary>
        public void SetScrub(int session, bool active, float pos)
        {
            _packet.scrub.session = session;
            _packet.scrub.active = active;
            _packet.scrub.pos = Mathf.Clamp01(pos);
        }

        /// <summary>Stop repeating a command once it is acked, or after
        /// commandRetrySeconds if nothing is listening (no echo receiver).</summary>
        void ExpireCommand()
        {
            if (_packet.cmd.id == 0) return;
            if (_packet.cmd.id <= AckedCommandId
                || Time.unscaledTime - _cmdSentAt > commandRetrySeconds)
            {
                _packet.cmd.id = 0;
                _packet.cmd.name = "";
                _packet.cmd.arg = 0f;
            }
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            int n = Mathf.Max(1, _beatSent);
            Debug.Log($"[VlaTeleop] hb sent:{_beatSent} ({_beatSent / heartbeatSeconds:0}/s)  "
                      + $"L:{100 * _beatLeft / n}% R:{100 * _beatRight / n}% "
                      + $"body:{100 * _beatBody / n}% legs:{100 * _beatLegs / n}% "
                      + $"lowConf:{_beatLowConf} sendErr:{_beatSendErr}"
                      + (_lastError != null ? $" last:{_lastError}" : ""));
            _beatSent = _beatLeft = _beatRight = _beatBody = _beatLegs = _beatLowConf = _beatSendErr = 0;
        }

        void FillBody(BodyPayload body)
        {
            bool ok = bodyAnchors != null && bodyAnchors.IsValid;
            body.valid = ok;
            body.legs_valid = ok && bodyAnchors.LegsValid;
            if (!ok) return;
            WriteRosVec(bodyAnchors.Chest, body.chest);
            WriteRosVec(bodyAnchors.LeftShoulder, body.l_shoulder);
            WriteRosVec(bodyAnchors.RightShoulder, body.r_shoulder);
            if (!body.legs_valid) return;
            WriteRosVec(bodyAnchors.LeftHip, body.l_hip);
            WriteRosVec(bodyAnchors.RightHip, body.r_hip);
            WriteRosVec(bodyAnchors.LeftKnee, body.l_knee);
            WriteRosVec(bodyAnchors.RightKnee, body.r_knee);
            WriteRosVec(bodyAnchors.LeftAnkle, body.l_ankle);
            WriteRosVec(bodyAnchors.RightAnkle, body.r_ankle);
            WriteRosVec(bodyAnchors.LeftToe, body.l_toe);
            WriteRosVec(bodyAnchors.RightToe, body.r_toe);
        }

        static void WriteRosVec(Vector3 u, float[] ros)
        {
            ros[0] = u.z; ros[1] = -u.x; ros[2] = u.y;
        }

        void FillHand(HandPayload payload, OVRHand hand, OVRSkeleton skel)
        {
            bool tracked = hand != null && hand.IsTracked && hand.IsDataValid
                           && QuestHandLandmarks.TryFill(skel, _world21);
            payload.visible = tracked;
            if (!tracked)
            {
                payload.conf = 0f;
                payload.wrist = new float[0];
                payload.wrist_quat = new float[0];
                payload.landmarks = new float[0];
                return;
            }
            payload.conf = hand.IsDataHighConfidence ? 1f : 0.5f;

            // 21 metric world joints (MediaPipe order) -> ROS frame, flat 63.
            if (payload.landmarks == null || payload.landmarks.Length != 63)
                payload.landmarks = new float[63];
            for (int i = 0; i < QuestHandLandmarks.Count; i++)
            {
                Vector3 w = _world21[i];
                payload.landmarks[i * 3 + 0] = w.z;
                payload.landmarks[i * 3 + 1] = -w.x;
                payload.landmarks[i * 3 + 2] = w.y;
            }

            // Wrist EE target = the TRUE metric wrist world position (landmark 0),
            // ROS frame. No heuristic depth — Quest's advantage over Viture.
            if (payload.wrist == null || payload.wrist.Length != 3)
                payload.wrist = new float[3];
            Vector3 wr = _world21[0];
            payload.wrist[0] = wr.z;
            payload.wrist[1] = -wr.x;
            payload.wrist[2] = wr.y;

            // Native wrist orientation (ROS xyzw) — spare data for wrist-
            // equipped robots; the H1 path ignores it.
            if (QuestHandLandmarks.TryWristRotation(skel, out Quaternion q))
            {
                if (payload.wrist_quat == null || payload.wrist_quat.Length != 4)
                    payload.wrist_quat = new float[4];
                payload.wrist_quat[0] = -q.z;
                payload.wrist_quat[1] = q.x;
                payload.wrist_quat[2] = -q.y;
                payload.wrist_quat[3] = q.w;
            }
            else
            {
                payload.wrist_quat = new float[0];
            }
        }

        static void WriteRosPose(Vector3 p, Quaternion q, float[] pos, float[] quat)
        {
            pos[0] = p.z; pos[1] = -p.x; pos[2] = p.y;
            quat[0] = -q.z; quat[1] = q.x; quat[2] = -q.y; quat[3] = q.w;
        }

        void OnGUI()
        {
            if (!showHud) return;
            string hands = _packet.left.visible || _packet.right.visible
                ? $"hands L:{HandMark(_packet.left)} R:{HandMark(_packet.right)}"
                : "hands: none";
            string body = _packet.body.valid
                ? (_packet.body.legs_valid ? "body:✓ legs:✓" : "body:✓ legs:–")
                : "body:–";
            string err = _lastError != null ? $"  send:{_lastError}" : "";
            string cmd = PendingCommand.Length > 0 ? $"  cmd:{PendingCommand}…" : "";
            string scrub = _packet.scrub.active
                ? $"  scrub:{_packet.scrub.pos:0.00}" : "";
            GUI.Label(new Rect(10, 10, 760, 22),
                      $"VLA teleop  sent:{_sent}  {hands}  {body}  " +
                      $"mode:{mode}{cmd}{scrub}{err}");
        }

        /// <summary>✓ high conf, ~ low conf (server HOLDs those frames), – untracked.</summary>
        static string HandMark(HandPayload h)
            => !h.visible ? "–" : (h.conf >= 0.9f ? "✓" : "~");
    }
}
