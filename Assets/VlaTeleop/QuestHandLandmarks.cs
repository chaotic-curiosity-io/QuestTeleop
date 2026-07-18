// VLA XR teleop — Quest port.
//
// Remaps a native Quest hand (OVRSkeleton) into MediaPipe's 21-landmark ordering
// so the openvla-unity-sim2real Python retargeters (handtracking/retarget.py)
// consume Quest hands UNCHANGED — exactly as they consume the Viture app's
// MediaPipe frames. The retargeters compute each finger "curl" as an INTERIOR
// joint angle from three landmark positions (finger_curl / thumb_signals), which
// is invariant to the frame the points live in, so all that matters is:
//
//   * the 21 points are in MediaPipe TOPOLOGY (wrist, then thumb/index/middle/
//     ring/pinky, 4 joints each), and
//   * they share one consistent, metric 3D frame.
//
// Unlike Viture (MediaPipe monocular, x/y normalized image coords + a relative
// z, so its angles are anisotropic guesses), Quest gives true metric world
// joints — the interior angles are geometrically correct. The one-time cost is
// that retarget.py's open/closed-degree thresholds were tuned against MediaPipe's
// distorted coords, so they may want a small retune for Quest (they're already
// exposed as retargeter RANGES + finger_curl open_deg/closed_deg args).
//
// Output is in Unity world space; VlaTeleopSender converts to the repo's ROS
// robot frame before it goes on the wire.

using UnityEngine;

namespace VlaTeleop
{
    /// <summary>
    /// Fills a 21-entry MediaPipe-ordered array of metric world joint positions
    /// from an <see cref="OVRSkeleton"/>. Static + allocation-light so the sender
    /// can call it every frame.
    /// </summary>
    public static class QuestHandLandmarks
    {
        // MediaPipe hand landmark ordering (21):
        //   0 wrist
        //   1..4  thumb  (CMC, MCP, IP, TIP)
        //   5..8  index  (MCP, PIP, DIP, TIP)
        //   9..12 middle
        //   13..16 ring
        //   17..20 pinky
        // Mapped to the OVRPlugin hand skeleton bone ids. OVR's thumb carries a
        // Thumb0 (trapezium/CMC) + Thumb1..3; we take Thumb0/1/2 for CMC/MCP/IP
        // and ThumbTip for the tip (Thumb3 has no MediaPipe equivalent). OVR's
        // Pinky0 is the metacarpal (no MediaPipe slot); Pinky1..3 + tip fill
        // MCP/PIP/DIP/TIP like the other fingers.
        static readonly OVRSkeleton.BoneId[] Map =
        {
            OVRSkeleton.BoneId.Hand_WristRoot,   // 0  wrist
            OVRSkeleton.BoneId.Hand_Thumb0,      // 1  thumb CMC
            OVRSkeleton.BoneId.Hand_Thumb1,      // 2  thumb MCP
            OVRSkeleton.BoneId.Hand_Thumb2,      // 3  thumb IP
            OVRSkeleton.BoneId.Hand_ThumbTip,    // 4  thumb TIP
            OVRSkeleton.BoneId.Hand_Index1,      // 5  index MCP
            OVRSkeleton.BoneId.Hand_Index2,      // 6  index PIP
            OVRSkeleton.BoneId.Hand_Index3,      // 7  index DIP
            OVRSkeleton.BoneId.Hand_IndexTip,    // 8  index TIP
            OVRSkeleton.BoneId.Hand_Middle1,     // 9  middle MCP
            OVRSkeleton.BoneId.Hand_Middle2,     // 10 middle PIP
            OVRSkeleton.BoneId.Hand_Middle3,     // 11 middle DIP
            OVRSkeleton.BoneId.Hand_MiddleTip,   // 12 middle TIP
            OVRSkeleton.BoneId.Hand_Ring1,       // 13 ring MCP
            OVRSkeleton.BoneId.Hand_Ring2,       // 14 ring PIP
            OVRSkeleton.BoneId.Hand_Ring3,       // 15 ring DIP
            OVRSkeleton.BoneId.Hand_RingTip,     // 16 ring TIP
            OVRSkeleton.BoneId.Hand_Pinky1,      // 17 pinky MCP
            OVRSkeleton.BoneId.Hand_Pinky2,      // 18 pinky PIP
            OVRSkeleton.BoneId.Hand_Pinky3,      // 19 pinky DIP
            OVRSkeleton.BoneId.Hand_PinkyTip,    // 20 pinky TIP
        };

        public const int Count = 21;   // MediaPipe landmark count

        /// <summary>
        /// Writes 21 metric world positions (MediaPipe order) into
        /// <paramref name="world"/> (length &gt;= 21). Returns false if the
        /// skeleton isn't ready or any required bone is missing — in which case
        /// the hand should be reported not-visible. Missing tip bones are
        /// tolerated by falling back to the parent joint so partial skeletons
        /// still yield usable finger angles.
        /// </summary>
        public static bool TryFill(OVRSkeleton skel, Vector3[] world)
        {
            if (skel == null || !skel.IsDataValid || !skel.IsDataHighConfidence
                || skel.Bones == null || skel.Bones.Count == 0 || world == null
                || world.Length < Count)
                return false;

            // OVRSkeleton.Bones is indexed by BoneId for hands, but we resolve by
            // Id to be robust to ordering/version differences.
            bool wroteWrist = false;
            for (int i = 0; i < Count; i++)
            {
                if (TryBone(skel, Map[i], out Vector3 p))
                {
                    world[i] = p;
                    if (i == 0) wroteWrist = true;
                }
                else if (i > 0)
                {
                    // Fall back to the previous joint in the finger (or wrist) so a
                    // momentarily-missing tip doesn't invalidate the whole hand.
                    world[i] = world[i - 1];
                }
                else
                {
                    return false;   // no wrist -> unusable
                }
            }
            return wroteWrist;
        }

        static bool TryBone(OVRSkeleton skel, OVRSkeleton.BoneId id, out Vector3 worldPos)
        {
            worldPos = default;
            foreach (var b in skel.Bones)
            {
                if (b != null && b.Id == id && b.Transform != null)
                {
                    worldPos = b.Transform.position;
                    return true;
                }
            }
            return false;
        }
    }
}
