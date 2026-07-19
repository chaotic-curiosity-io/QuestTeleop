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
        //
        // TWO bone-id maps, selected per skeleton at runtime: with the OpenXR
        // plugin (this project) OVRSkeleton reports SkeletonType.XRHandLeft/
        // Right whose Bones carry XRHand_* ids — the legacy Hand_* ids are a
        // DIFFERENT numeric range, so comparing against them silently matches
        // the wrong bones (that was the scrambled middle/ring + dead thumb).
        //
        // XR set (OpenXR EXT_hand_tracking joints): each *Proximal bone's
        // origin is the MCP joint, *Intermediate = PIP, *Distal = DIP.
        // Thumb: Metacarpal origin = CMC, Proximal = MCP, Distal = IP.
        static readonly OVRSkeleton.BoneId[] XrMap =
        {
            OVRSkeleton.BoneId.XRHand_Wrist,               // 0  wrist
            OVRSkeleton.BoneId.XRHand_ThumbMetacarpal,     // 1  thumb CMC
            OVRSkeleton.BoneId.XRHand_ThumbProximal,       // 2  thumb MCP
            OVRSkeleton.BoneId.XRHand_ThumbDistal,         // 3  thumb IP
            OVRSkeleton.BoneId.XRHand_ThumbTip,            // 4  thumb TIP
            OVRSkeleton.BoneId.XRHand_IndexProximal,       // 5  index MCP
            OVRSkeleton.BoneId.XRHand_IndexIntermediate,   // 6  index PIP
            OVRSkeleton.BoneId.XRHand_IndexDistal,         // 7  index DIP
            OVRSkeleton.BoneId.XRHand_IndexTip,            // 8  index TIP
            OVRSkeleton.BoneId.XRHand_MiddleProximal,      // 9  middle MCP
            OVRSkeleton.BoneId.XRHand_MiddleIntermediate,  // 10 middle PIP
            OVRSkeleton.BoneId.XRHand_MiddleDistal,        // 11 middle DIP
            OVRSkeleton.BoneId.XRHand_MiddleTip,           // 12 middle TIP
            OVRSkeleton.BoneId.XRHand_RingProximal,        // 13 ring MCP
            OVRSkeleton.BoneId.XRHand_RingIntermediate,    // 14 ring PIP
            OVRSkeleton.BoneId.XRHand_RingDistal,          // 15 ring DIP
            OVRSkeleton.BoneId.XRHand_RingTip,             // 16 ring TIP
            OVRSkeleton.BoneId.XRHand_LittleProximal,      // 17 pinky MCP
            OVRSkeleton.BoneId.XRHand_LittleIntermediate,  // 18 pinky PIP
            OVRSkeleton.BoneId.XRHand_LittleDistal,        // 19 pinky DIP
            OVRSkeleton.BoneId.XRHand_LittleTip,           // 20 pinky TIP
        };

        // Legacy OVR set: Thumb0 is the trapezium; joint ORIGINS put Thumb1 at
        // the CMC, Thumb2 at the MCP, Thumb3 at the IP (the old Thumb0/1/2 map
        // was off by one — dead thumb pitch). Pinky0 (metacarpal) has no
        // MediaPipe slot.
        static readonly OVRSkeleton.BoneId[] LegacyMap =
        {
            OVRSkeleton.BoneId.Hand_WristRoot,   // 0  wrist
            OVRSkeleton.BoneId.Hand_Thumb1,      // 1  thumb CMC
            OVRSkeleton.BoneId.Hand_Thumb2,      // 2  thumb MCP
            OVRSkeleton.BoneId.Hand_Thumb3,      // 3  thumb IP
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

        static OVRSkeleton.BoneId[] MapFor(OVRSkeleton skel)
        {
            var t = skel.GetSkeletonType();
            bool xr = t == OVRSkeleton.SkeletonType.XRHandLeft
                      || t == OVRSkeleton.SkeletonType.XRHandRight;
            return xr ? XrMap : LegacyMap;
        }

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

            // Resolve by bone Id against the map matching this skeleton's
            // version (XRHand_* under OpenXR, legacy Hand_* otherwise).
            var map = MapFor(skel);
            bool wroteWrist = false;
            for (int i = 0; i < Count; i++)
            {
                if (TryBone(skel, map[i], out Vector3 p))
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

        /// <summary>World-space wrist rotation (Hand_WristRoot bone). Quest's
        /// native wrist orientation — unused by the H1 (no wrist joints) but
        /// carried in the packet for wrist-equipped robots later.</summary>
        public static bool TryWristRotation(OVRSkeleton skel, out Quaternion worldRot)
        {
            worldRot = Quaternion.identity;
            if (skel == null || !skel.IsDataValid || skel.Bones == null) return false;
            var wristId = MapFor(skel)[0];
            foreach (var b in skel.Bones)
            {
                if (b != null && b.Id == wristId && b.Transform != null)
                {
                    worldRot = b.Transform.rotation;
                    return true;
                }
            }
            return false;
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
