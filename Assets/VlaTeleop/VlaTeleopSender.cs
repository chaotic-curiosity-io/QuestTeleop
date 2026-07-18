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
// Packet schema (identical to the Viture sender):
//   {"type":"xr_pose","seq":N,"t":ms,
//    "head_pos":[3],"head_quat":[4],
//    "left":{"visible":b,"wrist":[3],"landmarks":[63]}, "right":{...}}

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

        [Header("Debug")]
        public bool showHud = true;

        // ---- packet DTOs (JsonUtility) -------------------------------------- //
        [Serializable]
        public class HandPayload
        {
            public bool visible;
            public float[] wrist = Empty;       // ROS frame, meters
            public float[] landmarks = Empty;   // 21*(x,y,z), ROS frame, metric
            static readonly float[] Empty = new float[0];
        }

        [Serializable]
        public class XrPosePacket
        {
            public string type = "xr_pose";
            public int seq;
            public double t;                    // ms since startup
            public float[] head_pos = new float[3];
            public float[] head_quat = new float[4];
            public HandPayload left = new HandPayload();
            public HandPayload right = new HandPayload();
        }

        Socket _socket;
        readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        readonly XrPosePacket _packet = new XrPosePacket();

        /// <summary>The most recent packet built + sent (ROS robot frame). Read-only
        /// handle for overlay/gizmo tools (VlaTeleopGizmos) so they draw EXACTLY what
        /// went on the wire, not a re-derivation.</summary>
        public XrPosePacket LastPacket => _packet;

        readonly Vector3[] _world21 = new Vector3[QuestHandLandmarks.Count];
        float _nextSend;
        int _sent;
        string _lastError;

        void Start()
        {
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            if (headTransform == null && rig != null) headTransform = rig.centerEyeAnchor;
            if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;
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
                bool isLeft = type == OVRSkeleton.SkeletonType.HandLeft
                              || (type == OVRSkeleton.SkeletonType.None
                                  && h.name.ToLowerInvariant().Contains("left"));
                if (isLeft && leftHand == null) { leftHand = h; leftSkeleton = skel; }
                else if (!isLeft && rightHand == null) { rightHand = h; rightSkeleton = skel; }
            }
        }

        void OnDestroy()
        {
            try { _socket?.Close(); } catch { }
        }

        void Update()
        {
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 1f / Mathf.Max(5f, sendHz);

            _packet.seq = ++_sent;
            _packet.t = Time.unscaledTimeAsDouble * 1000.0;
            WriteRosPose(headTransform.position, headTransform.rotation,
                         _packet.head_pos, _packet.head_quat);

            FillHand(_packet.left, leftHand, leftSkeleton);
            FillHand(_packet.right, rightHand, rightSkeleton);

            byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(_packet));
            foreach (var ep in _targets)
            {
                try { _socket.SendTo(data, ep); _lastError = null; }
                catch (SocketException e) { _lastError = e.SocketErrorCode.ToString(); }
            }
        }

        void FillHand(HandPayload payload, OVRHand hand, OVRSkeleton skel)
        {
            bool tracked = hand != null && hand.IsTracked && hand.IsDataValid
                           && QuestHandLandmarks.TryFill(skel, _world21);
            payload.visible = tracked;
            if (!tracked)
            {
                payload.wrist = new float[0];
                payload.landmarks = new float[0];
                return;
            }

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
                ? $"hands L:{(_packet.left.visible ? "✓" : "–")} R:{(_packet.right.visible ? "✓" : "–")}"
                : "hands: none";
            string err = _lastError != null ? $"  send:{_lastError}" : "";
            GUI.Label(new Rect(10, 10, 640, 22),
                      $"VLA teleop  sent:{_sent}  {hands}{err}");
        }
    }
}
