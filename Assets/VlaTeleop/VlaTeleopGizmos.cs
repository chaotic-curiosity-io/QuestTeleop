// VLA XR teleop — Quest port of VitureUnity/Assets/Scripts/VlaTeleopGizmos.cs.
//
// Debug overlay for the VLA teleop uplink: draws, in world space, the SAME
// virtual torso the Python side (handtracking/xr_pose.py) builds from the
// headset pose, plus the shoulder->wrist vector that becomes the robot arm
// target — so you can SEE what the Quest is sending versus how it maps to the
// robot, before it ever reaches the robot scene.
//
// Reads VlaTeleopSender.LastPacket (the exact ROS-frame data on the wire) and
// converts back to Unity for drawing, so what you see == what Python receives.
//
// The anchor constants MUST match xr_pose.py (CHEST_BACK / CHEST_DOWN /
// SHOULDER_HALF). swapHands defaults OFF here: Quest reports true handedness
// (OVRSkeleton.HandLeft/HandRight), so left hand drives the left arm — no cross-
// body mirror, no --swap-hands (that flag is a Viture egocentric-MediaPipe fix).

#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class VlaTeleopGizmos : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("The teleop uplink to read. Auto-found on this GameObject / in the scene.")]
        public VlaTeleopSender sender;

        [Header("Torso anchor — MUST match xr_pose.py")]
        public float chestBack = 0.08f;     // CHEST_BACK
        public float chestDown = 0.33f;     // CHEST_DOWN
        public float shoulderHalf = 0.19f;  // SHOULDER_HALF

        [Header("Hand routing")]
        [Tooltip("Mirror run_xr_teleop.sh --swap-hands. Quest knows true handedness so this " +
                 "stays OFF (left hand -> left arm). Only turn ON if you deliberately launch " +
                 "the teleop server with --swap-hands.")]
        public bool swapHands = false;

        [Header("Robot-target preview")]
        [Tooltip("Your shoulder->wrist reach (m); teleop_server --human-reach.")]
        public float humanReach = 0.55f;
        [Tooltip("Robot arm reach (m): H1 0.59, GR-1 0.50, G1 0.38. Sets the clamp marker.")]
        public float robotReach = 0.59f;
        public bool drawRobotTargetMarker = true;

        [Header("Draw toggles")]
        public bool drawHead = true;
        public bool drawTorso = true;
        public bool drawTargets = true;
        public bool drawLabels = true;

        [Header("Style")]
        public float markerRadius = 0.02f;
        public Color headColor = new Color(0.4f, 0.8f, 1f);
        public Color torsoColor = new Color(0.7f, 0.7f, 0.7f);
        public Color leftColor = new Color(0f, 0.86f, 1f);
        public Color rightColor = new Color(1f, 0.47f, 0f);
        public Color robotColor = new Color(0.3f, 1f, 0.4f);
        public Color bodyColor = new Color(1f, 0.2f, 0.9f);   // measured (body-tracked) anchors

        // Inverse of VlaTeleopSender's Unity->ROS pos map (ros = (u.z,-u.x,u.y)).
        static Vector3 RosToUnity(float[] r) => new Vector3(-r[1], r[2], r[0]);

        void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;                     // no live data in edit mode
            if (sender == null) sender = GetComponent<VlaTeleopSender>();
            if (sender == null) sender = FindObjectOfType<VlaTeleopSender>();
            if (sender == null || sender.headTransform == null) return;
            var pkt = sender.LastPacket;
            if (pkt == null) return;

            Transform head = sender.headTransform;
            Vector3 headPos = head.position;
            Vector3 up = Vector3.up;
            Vector3 fwdFlat = Vector3.ProjectOnPlane(head.forward, up);
            if (fwdFlat.sqrMagnitude < 1e-6f) fwdFlat = head.forward;   // looking straight up/down
            fwdFlat.Normalize();
            Vector3 rightFlat = Vector3.Cross(up, fwdFlat).normalized;  // person's right (ROS -y)

            Vector3 chest = headPos - chestBack * fwdFlat - chestDown * up;
            Vector3 shR = chest + shoulderHalf * rightFlat;
            Vector3 shL = chest - shoulderHalf * rightFlat;

            if (drawHead)
            {
                Gizmos.color = headColor;
                Gizmos.DrawWireSphere(headPos, markerRadius * 1.2f);
                Gizmos.DrawLine(headPos, headPos + fwdFlat * 0.25f);   // heading (yaw)
            }

            if (drawTorso)
            {
                Gizmos.color = torsoColor;
                Gizmos.DrawLine(headPos, chest);
                Gizmos.DrawLine(shL, shR);
                Gizmos.DrawWireSphere(chest, markerRadius * 0.8f);
                Gizmos.DrawWireSphere(shL, markerRadius);
                Gizmos.DrawWireSphere(shR, markerRadius);
#if UNITY_EDITOR
                if (drawLabels)
                {
                    Handles.Label(shL + up * 0.03f, "L shoulder");
                    Handles.Label(shR + up * 0.03f, "R shoulder");
                }
#endif
            }

            // Body-tracked anchors from the wire packet: when valid, the server
            // IKs from THESE, so the shoulder->wrist arrows re-anchor to them
            // (virtual anchors above stay as the faint fallback reference).
            bool bodyValid = pkt.body != null && pkt.body.valid;
            if (bodyValid)
            {
                Vector3 bChest = RosToUnity(pkt.body.chest);
                Vector3 bL = RosToUnity(pkt.body.l_shoulder);
                Vector3 bR = RosToUnity(pkt.body.r_shoulder);
                Gizmos.color = bodyColor;
                Gizmos.DrawWireSphere(bChest, markerRadius * 0.8f);
                Gizmos.DrawSphere(bL, markerRadius * 0.9f);
                Gizmos.DrawSphere(bR, markerRadius * 0.9f);
                Gizmos.DrawLine(bL, bR);
#if UNITY_EDITOR
                if (drawLabels)
                {
                    Handles.Label(bL + up * 0.05f, "L shoulder (tracked)");
                    Handles.Label(bR + up * 0.05f, "R shoulder (tracked)");
                }
#endif
                shL = bL;                      // arrows anchor where the server anchors
                shR = bR;
            }

            if (drawTargets)
            {
                DrawHand(swapHands ? "L-hand→R arm" : "L-hand→L arm", pkt.left,
                         swapHands ? shR : shL, fwdFlat, rightFlat, up,
                         swapHands ? rightColor : leftColor);
                DrawHand(swapHands ? "R-hand→L arm" : "R-hand→R arm", pkt.right,
                         swapHands ? shL : shR, fwdFlat, rightFlat, up,
                         swapHands ? leftColor : rightColor);
            }
        }

        void DrawHand(string name, VlaTeleopSender.HandPayload h, Vector3 shoulder,
                      Vector3 fwd, Vector3 right, Vector3 up, Color col)
        {
            if (h == null || !h.visible || h.wrist == null || h.wrist.Length != 3) return;

            Vector3 wrist = RosToUnity(h.wrist);
            Gizmos.color = col;
            Gizmos.DrawSphere(wrist, markerRadius);
            DrawArrow(shoulder, wrist, col);

            Vector3 v = wrist - shoulder;                        // = arm_ik target (pre-scale)
            float f = Vector3.Dot(v, fwd);
            float l = Vector3.Dot(v, -right);                    // person's left = ROS +y
            float u = Vector3.Dot(v, up);

            if (drawRobotTargetMarker && v.sqrMagnitude > 1e-6f)
            {
                float scaled = v.magnitude * (robotReach / Mathf.Max(0.01f, humanReach));
                float len = Mathf.Min(scaled, robotReach);       // reach clamp (like ArmIK)
                Vector3 rt = shoulder + v.normalized * len;
                Gizmos.color = robotColor;
                Gizmos.DrawWireSphere(rt, markerRadius * 0.9f);
                Gizmos.DrawLine(shoulder, rt);
            }
#if UNITY_EDITOR
            if (drawLabels)
            {
                Handles.Label(wrist + up * 0.04f,
                    $"{name}  f={f:+0.00;-0.00} l={l:+0.00;-0.00} u={u:+0.00;-0.00}   |{v.magnitude:0.00}m|");
            }
#endif
        }

        static void DrawArrow(Vector3 a, Vector3 b, Color col)
        {
            Gizmos.color = col;
            Gizmos.DrawLine(a, b);
            Vector3 dir = b - a;
            float len = dir.magnitude;
            if (len < 1e-4f) return;
            dir /= len;
            Vector3 ortho = Vector3.Cross(dir, Vector3.up);
            if (ortho.sqrMagnitude < 1e-5f) ortho = Vector3.Cross(dir, Vector3.forward);
            ortho.Normalize();
            float hs = Mathf.Min(0.05f, len * 0.2f);
            Gizmos.DrawLine(b, b - dir * hs + ortho * hs * 0.5f);
            Gizmos.DrawLine(b, b - dir * hs - ortho * hs * 0.5f);
        }
    }
}
