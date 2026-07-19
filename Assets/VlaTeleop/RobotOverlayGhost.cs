// VLA XR teleop — kinematic robot "ghost" overlay (FK poser).
//
// A physics-free Transform hierarchy of the robot (built from its URDF by
// Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost) that this component
// poses from the joint-target packets the teleop server echoes back
// (handtracking/target_echo.py -> RobotOverlayDriver -> SetTargets here).
// No ArticulationBody, no colliders — just localRotation FK, so it runs
// happily on-device at any rate.
//
// Frame math (must mirror the builder, which mirrors the repo convention):
//   ROS -> Unity position/axis: (x,y,z)ros -> (-y, z, x)unity
//   A rotation of +θ about a ROS axis is a rotation of −θ about the mapped
//   Unity axis (right-handed -> left-handed flip), so:
//       joint.localRotation = rest * AngleAxis(-θ·Rad2Deg, axisUnity)
//
// Mimic joints (the Inspire finger intermediate/distal links) are not in the
// 33-dim target vector; their angle is derived per frame from their source
// joint (θ = source·multiplier + offset), exactly like urdf_rerun.py.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class RobotOverlayGhost : MonoBehaviour
    {
        [Serializable]
        public class JointEntry
        {
            public string name;                 // URDF joint name
            public Transform joint;             // CHILD link's transform
            public Vector3 axisUnity;           // URDF axis mapped ROS->Unity
            public bool isMimic;
            public string mimicSource;          // URDF name of the driving joint
            public float mimicMultiplier = 1f;
            public float mimicOffset;
        }

        [Tooltip("All movable URDF joints (actuated + mimic), filled by the builder.")]
        public JointEntry[] joints = new JointEntry[0];

        [Tooltip("Vertical distance from the root (pelvis) origin to the lowest " +
                 "mesh point at rest pose — used to stand the ghost on the floor.")]
        public float feetToPelvis = 0.98f;

        public string robotName = "h1";

        Quaternion[] _rest;                     // authored (zero-pose) localRotation
        float[] _angle;                         // current angle per entry, radians
        Dictionary<string, int> _byName;

        void Awake()
        {
            CacheRest();
        }

        /// <summary>Snapshot the authored local rotations as the URDF zero pose.
        /// Call again only if the hierarchy was rebuilt at rest.</summary>
        public void CacheRest()
        {
            _rest = new Quaternion[joints.Length];
            _angle = new float[joints.Length];
            _byName = new Dictionary<string, int>(joints.Length);
            for (int i = 0; i < joints.Length; i++)
            {
                _rest[i] = joints[i].joint != null ? joints[i].joint.localRotation
                                                   : Quaternion.identity;
                _byName[joints[i].name] = i;
            }
        }

        /// <summary>Apply a joint_targets packet (radians, keyed by URDF joint
        /// name — unknown names ignored, so any robot's packet is safe).
        /// Returns how many actuated joints matched.</summary>
        public int SetTargets(string[] names, float[] targets)
        {
            if (_byName == null || _rest == null || _rest.Length != joints.Length)
                CacheRest();
            int n = Mathf.Min(names != null ? names.Length : 0,
                              targets != null ? targets.Length : 0);
            int applied = 0;
            for (int k = 0; k < n; k++)
            {
                if (_byName.TryGetValue(names[k], out int i) && !joints[i].isMimic)
                {
                    _angle[i] = targets[k];
                    applied++;
                }
            }
            for (int i = 0; i < joints.Length; i++)
            {
                var e = joints[i];
                if (e.isMimic && _byName.TryGetValue(e.mimicSource, out int s))
                    _angle[i] = _angle[s] * e.mimicMultiplier + e.mimicOffset;
            }
            Apply();
            return applied;
        }

        void Apply()
        {
            for (int i = 0; i < joints.Length; i++)
            {
                var e = joints[i];
                if (e.joint == null) continue;
                e.joint.localRotation = _rest[i] *
                    Quaternion.AngleAxis(-_angle[i] * Mathf.Rad2Deg, e.axisUnity);
            }
        }
    }
}
