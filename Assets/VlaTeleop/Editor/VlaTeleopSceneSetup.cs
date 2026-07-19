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

            // Body-tracked shoulder anchors: OVRBody poses are tracking-space,
            // so the anchors GameObject lives UNDER trackingSpace (identity).
            var anchors = Object.FindObjectOfType<QuestBodyAnchors>();
            if (anchors == null && rig.trackingSpace != null)
            {
                var go = new GameObject("BodyTrackingAnchors");
                Undo.RegisterCreatedObjectUndo(go, "Create BodyTrackingAnchors");
                go.transform.SetParent(rig.trackingSpace, false);
                anchors = go.AddComponent<QuestBodyAnchors>();
                anchors.trackingSpace = rig.trackingSpace;
            }
            sender.bodyAnchors = anchors;
            EnableBodyTracking();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = teleop;

            Debug.Log(
                "[VlaTeleop] VlaTeleopSender wired to OVRCameraRig.centerEyeAnchor -> udp " +
                "127.0.0.1:9905 (teleop server) + :9906 (DevVLA head camera). Hands: " +
                (haveHands ? "native OVRHand found (auto-bound at Play)."
                           : "NONE in scene — enable Hand Tracking and use a rig with " +
                             "OVRHand anchors, or the stream is head-only.") +
                " Body anchors: " + (sender.bodyAnchors != null
                    ? "QuestBodyAnchors wired (measured shoulders when tracking is up)."
                    : "NOT wired (no trackingSpace?) — server falls back to the virtual chest.") +
                "\nNext: 1) Tools ▸ Robot Teleop ▸ Start Teleop Server (Quest → H1) — " +
                "runs teleop_server.py --source quest-xr (exact URDF arm map + " +
                "profiles/quest_h1.json, NO --swap-hands); 2) in DevVLA run VLA/Setup Hand " +
                "Humanoid Scene/Unitree H1 + VLA/Teleop Mode/Arms + Hands + Head (Viture) " +
                "and press Play; 3) press Play here (over Link, enable Developer runtime " +
                "features + body tracking in the Meta Quest Link app, or build to device).");
        }

        [MenuItem("Tools/Robot Teleop/Open README", priority = 20)]
        public static void OpenReadme()
        {
            var path = "Assets/VlaTeleop/README.md";
            var obj = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (obj != null) AssetDatabase.OpenAsset(obj);
            else Debug.LogWarning($"[VlaTeleop] README not found at {path}");
        }

        /// <summary>Body tracking needs OVRManager consent: the on-startup
        /// permission request (device builds) — set via SerializedObject since
        /// the field is internal. Over Link, enable "Developer runtime
        /// features" + body tracking in the Meta Quest Link PC app.</summary>
        static void EnableBodyTracking()
        {
            var mgr = Object.FindObjectOfType<OVRManager>();
            if (mgr == null)
            {
                Debug.LogWarning("[VlaTeleop] No OVRManager in scene — body tracking "
                                 + "permission not configured.");
                return;
            }
            var so = new SerializedObject(mgr);
            var prop = so.FindProperty("requestBodyTrackingPermissionOnStartup");
            if (prop != null && !prop.boolValue)
            {
                prop.boolValue = true;
                so.ApplyModifiedProperties();
                Debug.Log("[VlaTeleop] OVRManager: body-tracking permission request "
                          + "on startup ENABLED.");
            }
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
