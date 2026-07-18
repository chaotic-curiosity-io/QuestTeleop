// VLA XR teleop — Quest port. Editor tooling.
//
// Adds "Tools/Robot Teleop/…" menu items that wire the VlaTeleopSender (+ debug
// gizmos) into a scene that already has an OVRCameraRig with hand tracking (any
// of the PassthroughCameraApiSamples scenes, or a ReachyBridge-set-up scene).
// The sender then streams head + native-hand landmarks over UDP to the
// openvla-unity-sim2real teleop server (:9905) and DevVLA (:9906).
//
// Menu (matches VitureUnity's menu path so the two apps read the same):
//   Tools/Robot Teleop/Add VLA Teleop Sender   add + wire RobotTeleop onto the scene
//   Tools/Robot Teleop/Open README             the VlaTeleop setup guide

using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class VlaTeleopSceneSetup
    {
        [MenuItem("Tools/Robot Teleop/Add VLA Teleop Sender", priority = 0)]
        public static void AddSender()
        {
            var rig = Object.FindObjectOfType<OVRCameraRig>();
            if (rig == null)
            {
                EditorUtility.DisplayDialog(
                    "No OVRCameraRig",
                    "This scene has no OVRCameraRig. Open a passthrough sample scene " +
                    "(CameraViewer / CameraToWorld) or run ReachyBridge ▸ Set Up Active " +
                    "Scene first, then re-run this.",
                    "OK");
                return;
            }

            bool haveHands = Object.FindObjectsOfType<OVRHand>(includeInactive: true).Length > 0;

            GameObject teleop = GetOrCreate("RobotTeleop");
            var sender = GetOrAdd<VlaTeleopSender>(teleop);
            sender.rig = rig;
            sender.headTransform = rig.centerEyeAnchor;   // sender also falls back to this

            var gizmos = GetOrAdd<VlaTeleopGizmos>(teleop);
            gizmos.sender = sender;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = teleop;

            Debug.Log(
                "[VlaTeleop] VlaTeleopSender wired to OVRCameraRig.centerEyeAnchor -> udp " +
                "127.0.0.1:9905 (teleop server) + :9906 (DevVLA head camera). Hands: " +
                (haveHands ? "native OVRHand found (auto-bound at Play)."
                           : "NONE in scene — enable Hand Tracking and use a rig with " +
                             "OVRHand anchors, or the stream is head-only.") +
                "\nNext: 1) build+run on the Quest (ReachyBridge ▸ Build and Run works for " +
                "this scene too); 2) on the Mac: openvla-unity-sim2real/handtracking/" +
                "run_xr_teleop.sh --robot h1  (NO --swap-hands — Quest knows handedness); " +
                "point the headset's UDP host at the Mac if not on localhost (edit the " +
                "sender's endpoints); 3) in DevVLA run VLA/Setup Hand Humanoid Scene/Unitree " +
                "H1 + VLA/Teleop Mode/Arms + Hands + Head (Viture) and press Play.");
        }

        [MenuItem("Tools/Robot Teleop/Open README", priority = 20)]
        public static void OpenReadme()
        {
            var path = "Assets/VlaTeleop/README.md";
            var obj = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (obj != null) AssetDatabase.OpenAsset(obj);
            else Debug.LogWarning($"[VlaTeleop] README not found at {path}");
        }

        // ---- helpers ------------------------------------------------------- //

        static GameObject GetOrCreate(string name)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            }
            return go;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c == null) c = Undo.AddComponent<T>(go);
            return c;
        }
    }
}
