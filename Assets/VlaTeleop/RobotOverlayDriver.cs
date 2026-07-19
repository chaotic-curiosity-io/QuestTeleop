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
//   Superimposed — the ghost stands where YOU stand: feet on the tracking-
//                  space floor, XZ under your chest (body-tracked) or head,
//                  yaw from your measured shoulder line (head yaw fallback).
//                  You look at it from inside; glance down at your arms, or
//                  use InFront for a full view. Backface culling means the
//                  torso shell mostly vanishes from inside.
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
        [Tooltip("Root anchor smoothing rates (1/s). Joint angles are NOT smoothed.")]
        public float positionLerp = 8f;
        public float rotationLerp = 6f;

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
        bool _inFrontAnchored;
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
                    _lastSeq = pkt.seq;
                    _lastRobot = pkt.robot;
                    _beatPackets++;
                }
            }
            Heartbeat();
        }

        void LateUpdate()
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
                targetPos = new Vector3(refPos.x, floorY + ghost.feetToPelvis, refPos.z);
                targetRot = Quaternion.LookRotation(fwd);
                _inFrontAnchored = false;          // re-anchor next InFront switch
            }

            float dt = Time.deltaTime;
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

        /// <summary>InFront mode: re-anchor at the player's current pose.</summary>
        [ContextMenu("Recenter")]
        public void Recenter()
        {
            _inFrontAnchored = false;
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            Debug.Log($"[RobotOverlay] hb pkts:{_beatPackets} " +
                      $"({_beatPackets / heartbeatSeconds:0.0}/s) robot:{_lastRobot} " +
                      $"seq:{_lastSeq} joints:{_appliedJoints}/{ghost.joints.Length} " +
                      $"mode:{mode} anchor:{(_beatBodyAnchored > 0 ? "body" : "head")}" +
                      (_beatPackets == 0 ? "  (no echo — server running with echo_targets on?)" : ""));
            _beatPackets = 0;
            _beatBodyAnchored = 0;
        }

        void OnGUI()
        {
            if (!showHud) return;
            string state = _lastSeq >= 0
                ? $"robot:{_lastRobot} seq:{_lastSeq} joints:{_appliedJoints}"
                : "waiting for joint_targets echo";
            GUI.Label(new Rect(10, 34, 640, 22),
                      $"Robot ghost :{listenPort}  {state}  mode:{mode}");
        }
    }
}
