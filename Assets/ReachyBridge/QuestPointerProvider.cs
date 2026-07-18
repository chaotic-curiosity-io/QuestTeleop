// ReachyBrain XR bridge — Quest port.
//
// Supplies the per-hand pointing state that ReachyBridgeSender streams to the
// Mac server (follow_hand / raycast). This is the Quest equivalent of the
// Viture app's VitureHandWorldVisualizer + VitureHandPinchGrabber math, but:
//
//   * hand joints come from the native OVRSkeleton and are already METRIC
//     world positions (Viture had to fake a 0.6 m depth through MediaPipe), and
//   * pointing auto-switches between the Touch controller ray and the hand
//     PointerPose depending on what the user is holding, mirroring how the Meta
//     PassthroughCameraApiSamples StartScene already flips hand<->controller.
//
// All vectors are Unity world space (left-handed, Y-up, meters) — the server
// owns the Unity->robot registration, exactly as with the Viture sender.

using UnityEngine;

namespace ReachyBridge
{
    /// <summary>One hand's pointing state in Unity world space.</summary>
    public struct HandPointer
    {
        public bool visible;
        public Vector3 palm;       // a point on/near the palm (follow_hand target)
        public Vector3 rayOrigin;  // raycast start
        public Vector3 rayDir;     // raycast direction (normalized)
        public bool pinch;         // "select" gesture / trigger
    }

    /// <summary>
    /// Resolves left/right <see cref="HandPointer"/> from whichever input is
    /// active (Touch controllers or hand tracking). Add it next to an
    /// <see cref="OVRCameraRig"/>; it finds the rig, the two <see cref="OVRHand"/>
    /// components and their skeletons automatically, or you can assign them.
    /// </summary>
    public class QuestPointerProvider : MonoBehaviour
    {
        [Header("Rig / hands (auto-found if left empty)")]
        public OVRCameraRig rig;
        public OVRHand leftHand;
        public OVRHand rightHand;
        public OVRSkeleton leftSkeleton;
        public OVRSkeleton rightSkeleton;

        [Header("Controller pointing")]
        [Tooltip("Index-trigger pull (0..1) above which a controller counts as pinching/selecting.")]
        [Range(0.1f, 0.95f)] public float controllerPinchThreshold = 0.5f;

        void Awake()
        {
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            AutoFindHands();
        }

        void AutoFindHands()
        {
            if (leftHand != null && rightHand != null) return;
            foreach (var h in FindObjectsOfType<OVRHand>(includeInactive: true))
            {
                // OVRHand and its OVRSkeleton live on the same GameObject.
                var skel = h.GetComponent<OVRSkeleton>();
                var type = h.GetComponent<OVRSkeleton>() != null
                    ? skel.GetSkeletonType()
                    : OVRSkeleton.SkeletonType.None;
                bool isLeft = type == OVRSkeleton.SkeletonType.HandLeft
                              || h.name.ToLowerInvariant().Contains("left");
                if (isLeft && leftHand == null) { leftHand = h; leftSkeleton = skel; }
                else if (!isLeft && rightHand == null) { rightHand = h; rightSkeleton = skel; }
            }
        }

        /// <summary>0 = Left, 1 = Right (matches the server's "Left"/"Right" hand ids).</summary>
        public bool TryGetPointer(int handIndex, out HandPointer p)
        {
            p = default;
            bool left = handIndex == 0;
            var ovrHand = left ? leftHand : rightHand;
            var skel = left ? leftSkeleton : rightSkeleton;

            // Prefer hand tracking when the hand is actually tracked; otherwise
            // fall back to the Touch controller on that side.
            if (ovrHand != null && ovrHand.IsTracked && ovrHand.IsDataValid)
                return TryHand(ovrHand, skel, out p);

            return TryController(left, out p);
        }

        // ---------------------------------------------------------------- //
        // Hand tracking                                                     //
        // ---------------------------------------------------------------- //

        bool TryHand(OVRHand hand, OVRSkeleton skel, out HandPointer p)
        {
            p = default;

            // Palm = centroid of wrist + index-MCP + pinky-MCP (same three joints
            // the Viture palm-centroid used). Falls back gracefully if the
            // skeleton hasn't populated yet.
            Vector3 wrist, indexMcp, pinkyMcp;
            bool haveW = TryBone(skel, OVRSkeleton.BoneId.Hand_WristRoot, out wrist);
            bool haveI = TryBone(skel, OVRSkeleton.BoneId.Hand_Index1, out indexMcp);
            bool haveP = TryBone(skel, OVRSkeleton.BoneId.Hand_Pinky1, out pinkyMcp);

            Vector3 palm;
            if (haveW && haveI && haveP) palm = (wrist + indexMcp + pinkyMcp) / 3f;
            else if (haveW) palm = wrist;
            else if (hand.IsPointerPoseValid) palm = hand.PointerPose.position;
            else return false;

            // Ray = Meta's stabilized pointer aim when available (better than a
            // reconstructed palm normal). Fall back to the palm-plane normal
            // (Viture's math) so raycast still works without a valid pointer pose.
            Vector3 rayO, rayD;
            if (hand.IsPointerPoseValid)
            {
                rayO = hand.PointerPose.position;
                rayD = hand.PointerPose.forward;
            }
            else if (haveW && haveI && haveP)
            {
                var normal = Vector3.Cross(indexMcp - wrist, pinkyMcp - wrist);
                // Right-hand palm normal points the opposite way (Viture parity).
                if (skel != null && skel.GetSkeletonType() == OVRSkeleton.SkeletonType.HandRight)
                    normal = -normal;
                rayO = palm;
                rayD = normal.sqrMagnitude > 1e-8f ? normal.normalized : (indexMcp - wrist).normalized;
            }
            else
            {
                return false;
            }

            p.visible = true;
            p.palm = palm;
            p.rayOrigin = rayO;
            p.rayDir = rayD.sqrMagnitude > 1e-8f ? rayD.normalized : Vector3.forward;
            p.pinch = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
            return true;
        }

        static bool TryBone(OVRSkeleton skel, OVRSkeleton.BoneId id, out Vector3 worldPos)
        {
            worldPos = default;
            if (skel == null || !skel.IsDataValid || skel.Bones == null) return false;
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

        // ---------------------------------------------------------------- //
        // Touch controllers                                                 //
        // ---------------------------------------------------------------- //

        bool TryController(bool left, out HandPointer p)
        {
            p = default;
            var controller = left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            if ((OVRInput.GetConnectedControllers() & controller) == 0) return false;

            // Prefer the rig's controller anchor (already in world space and
            // pose-corrected). Fall back to tracking-space local pose if needed.
            Transform anchor = rig != null
                ? (left ? rig.leftControllerAnchor : rig.rightControllerAnchor)
                : null;

            Vector3 pos, fwd;
            if (anchor != null)
            {
                pos = anchor.position;
                fwd = anchor.forward;
            }
            else if (rig != null && rig.trackingSpace != null)
            {
                var lp = OVRInput.GetLocalControllerPosition(controller);
                var lr = OVRInput.GetLocalControllerRotation(controller);
                pos = rig.trackingSpace.TransformPoint(lp);
                fwd = rig.trackingSpace.rotation * (lr * Vector3.forward);
            }
            else
            {
                return false;
            }

            float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
            p.visible = true;
            p.palm = pos;
            p.rayOrigin = pos;
            p.rayDir = fwd.sqrMagnitude > 1e-8f ? fwd.normalized : Vector3.forward;
            p.pinch = trigger >= controllerPinchThreshold;
            return true;
        }

        /// <summary>The pointer currently doing the "pointing" (for a laser/HUD),
        /// preferring the right hand/controller, then the left.</summary>
        public bool TryGetActivePointer(out HandPointer p)
        {
            if (TryGetPointer(1, out p) && p.visible) return true;
            if (TryGetPointer(0, out p) && p.visible) return true;
            p = default;
            return false;
        }
    }
}
