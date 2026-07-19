// VLA XR teleop — Quest body-tracked torso anchors.
//
// Reads Meta body tracking (core-SDK OVRBody, the same tracker the Movement
// SDK's MovementBody sample renders) and exposes WORLD-space chest + shoulder
// positions for the teleop uplink. When these are valid the Python side
// anchors each arm's IK to your MEASURED shoulder instead of the virtual
// head-offset chest heuristic (xr_pose.py CHEST_*/SHOULDER_HALF) — the
// heuristic remains the automatic fallback whenever body tracking drops out.
//
// "Shoulder" here = Body_LeftArmUpper / Body_RightArmUpper (the upper-arm
// head, i.e. the anatomical shoulder joint), not Body_*Shoulder (clavicle).
//
// OVRBody poses are TRACKING-space; world = trackingSpace.TransformPoint(...).
// Keep this component on a GameObject wired to the rig's trackingSpace (the
// Tools ▸ Robot Teleop ▸ Add VLA Teleop Sender menu does this), or leave
// trackingSpace null to fall back to this GameObject's own transform.

using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class QuestBodyAnchors : MonoBehaviour
    {
        [Tooltip("Body-tracking data source. Auto-added on this GameObject if empty.")]
        public OVRBody body;
        [Tooltip("OVRCameraRig.trackingSpace — OVRBody poses are tracking-space. " +
                 "Null = use this GameObject's transform.")]
        public Transform trackingSpace;

        [Tooltip("Request the FullBody joint set (generative legs) so hip/knee/" +
                 "ankle anchors stream too. Falls back gracefully if the runtime " +
                 "only provides the upper body.")]
        public bool requestFullBody = true;

        /// <summary>All three torso anchors had valid positions this frame.</summary>
        public bool IsValid { get; private set; }
        /// <summary>All eight leg joints had valid positions this frame
        /// (needs the FullBody joint set — Quest legs are synthesized).</summary>
        public bool LegsValid { get; private set; }
        /// <summary>Tracker confidence 0..1 of the last valid frame.</summary>
        public float Confidence { get; private set; }
        public Vector3 Chest { get; private set; }         // world
        public Vector3 LeftShoulder { get; private set; }  // world
        public Vector3 RightShoulder { get; private set; } // world
        public Vector3 LeftHip { get; private set; }
        public Vector3 RightHip { get; private set; }
        public Vector3 LeftKnee { get; private set; }
        public Vector3 RightKnee { get; private set; }
        public Vector3 LeftAnkle { get; private set; }
        public Vector3 RightAnkle { get; private set; }
        public Vector3 LeftToe { get; private set; }
        public Vector3 RightToe { get; private set; }

        void Awake()
        {
            if (body == null) body = GetComponent<OVRBody>();
            if (body == null) body = gameObject.AddComponent<OVRBody>();
            if (requestFullBody)
                body.ProvidedSkeletonType = OVRPlugin.BodyJointSet.FullBody;
        }

        void Update()
        {
            IsValid = false;
            LegsValid = false;
            var state = body != null ? body.BodyState : null;
            if (state == null) return;
            var joints = state.Value.JointLocations;
            // The RUNTIME decides what it actually delivered: FullBody arrays
            // are longer, so probe by array length via the leg joint index.
            bool full = joints != null
                        && joints.Length > (int)OVRPlugin.BoneId.FullBody_LeftUpperLeg;

            if (!TryJoint(joints,
                    full ? OVRPlugin.BoneId.FullBody_Chest : OVRPlugin.BoneId.Body_Chest,
                    out Vector3 chest)
                || !TryJoint(joints,
                    full ? OVRPlugin.BoneId.FullBody_LeftArmUpper : OVRPlugin.BoneId.Body_LeftArmUpper,
                    out Vector3 lsh)
                || !TryJoint(joints,
                    full ? OVRPlugin.BoneId.FullBody_RightArmUpper : OVRPlugin.BoneId.Body_RightArmUpper,
                    out Vector3 rsh))
                return;

            Chest = chest;
            LeftShoulder = lsh;
            RightShoulder = rsh;
            Confidence = state.Value.Confidence;
            IsValid = true;

            if (!full) return;
            if (TryJoint(joints, OVRPlugin.BoneId.FullBody_LeftUpperLeg, out Vector3 lHip)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_RightUpperLeg, out Vector3 rHip)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_LeftLowerLeg, out Vector3 lKnee)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_RightLowerLeg, out Vector3 rKnee)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_LeftFootAnkle, out Vector3 lAnk)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_RightFootAnkle, out Vector3 rAnk)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_LeftFootBall, out Vector3 lToe)
                && TryJoint(joints, OVRPlugin.BoneId.FullBody_RightFootBall, out Vector3 rToe))
            {
                LeftHip = lHip; RightHip = rHip;
                LeftKnee = lKnee; RightKnee = rKnee;
                LeftAnkle = lAnk; RightAnkle = rAnk;
                LeftToe = lToe; RightToe = rToe;
                LegsValid = true;
            }
        }

        bool TryJoint(OVRPlugin.BodyJointLocation[] joints, OVRPlugin.BoneId id,
                      out Vector3 world)
        {
            world = default;
            int i = (int)id;
            if (joints == null || i < 0 || i >= joints.Length) return false;
            var loc = joints[i];
            if (!loc.PositionValid) return false;
            Vector3 local = loc.Pose.Position.FromFlippedZVector3f();
            Transform space = trackingSpace != null ? trackingSpace : transform;
            world = space.TransformPoint(local);
            return true;
        }
    }
}
