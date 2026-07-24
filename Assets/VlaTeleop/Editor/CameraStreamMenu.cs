// VLA XR teleop — robot camera overlay menu (editor only).
//
// Adds/removes the RobotCameraOverlay in the open scene: a UDP :9908 listener
// that shows the robot's camera view (streamed by DevVLA's RobotCameraStreamer)
// on a panel anchored in front of the user. Optional — nothing else depends
// on it. DevVLA side: VLA Control Panel > Teleop Mode > Enable Robot Camera
// Stream.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class CameraStreamMenu
    {
        const string OverlayName = "RobotCameraOverlay";

        [MenuItem("Tools/Robot Teleop/Camera Stream/Enable Robot Camera Overlay", priority = 60)]
        public static void EnableOverlay()
        {
            var existing = Object.FindObjectOfType<RobotCameraOverlay>(true);
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                existing.enabled = true;
                EditorUtility.SetDirty(existing);
                Selection.activeGameObject = existing.gameObject;
                Finish($"[CameraStream] Re-enabled existing '{existing.name}'.");
                return;
            }
            var go = new GameObject(OverlayName);
            Undo.RegisterCreatedObjectUndo(go, "Enable robot camera overlay");
            go.AddComponent<RobotCameraOverlay>();
            Selection.activeGameObject = go;
            Finish("[CameraStream] RobotCameraOverlay added (udp :9908). Enter Play with the " +
                   "teleop scene running; DevVLA must have its Robot Camera Stream enabled and " +
                   "be receiving this headset's xr_pose packets (that's how it learns our IP).");
        }

        [MenuItem("Tools/Robot Teleop/Camera Stream/Disable Robot Camera Overlay", priority = 61)]
        public static void DisableOverlay()
        {
            var overlay = Object.FindObjectOfType<RobotCameraOverlay>(true);
            if (overlay == null)
            {
                Debug.Log("[CameraStream] No RobotCameraOverlay in the open scene.");
                return;
            }
            Undo.DestroyObjectImmediate(overlay.gameObject);
            Finish("[CameraStream] RobotCameraOverlay removed.");
        }

        static void Finish(string msg)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log(msg);
        }
    }
}
