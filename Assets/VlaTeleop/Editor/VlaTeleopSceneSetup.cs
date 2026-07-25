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
//   Tools/Robot Teleop/Gestures/…              in-VR transport (TeleopGestureSetup.cs)
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

            // The ghost overlay (if built) anchors to the same body data, and
            // acknowledges transport commands back to this sender.
            var overlay = Object.FindObjectOfType<RobotOverlayDriver>();
            if (overlay != null)
            {
                if (overlay.bodyAnchors == null) overlay.bodyAnchors = anchors;
                if (overlay.sender == null) overlay.sender = sender;
            }

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

        /// <summary>In-VR metrics panel — the OnGUI HUDs only show on the PC
        /// mirror view; this one floats in front of you in the headset.</summary>
        [MenuItem("Tools/Robot Teleop/Add Floating HUD Panel", priority = 2)]
        public static void AddHudPanel()
        {
            var panel = Object.FindObjectOfType<VlaTeleopHudPanel>();
            if (panel == null)
            {
                GameObject teleop = GetOrCreate("RobotTeleop");
                panel = Undo.AddComponent<VlaTeleopHudPanel>(teleop);
            }
            panel.sender = Object.FindObjectOfType<VlaTeleopSender>();
            panel.overlay = Object.FindObjectOfType<RobotOverlayDriver>();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = panel.gameObject;
            Debug.Log("[VlaTeleop] Floating HUD panel added (lazy-follow, "
                      + "0.9 m ahead). Toggle via its 'visible' checkbox.");
        }

        [MenuItem("Tools/Robot Teleop/Open README", priority = 20)]
        public static void OpenReadme()
        {
            var path = "Assets/VlaTeleop/README.md";
            var obj = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (obj != null) AssetDatabase.OpenAsset(obj);
            else Debug.LogWarning($"[VlaTeleop] README not found at {path}");
        }

        /// <summary>Body tracking needs FOUR switches; this flips them all:
        /// 1. OVRProjectConfig.bodyTrackingSupport (build capability),
        /// 2. the BODY_TRACKING permission + feature in the custom
        ///    AndroidManifest (kept in sync here since the project overrides
        ///    the generated manifest),
        /// 3. OVRManager.requestBodyTrackingPermissionOnStartup (runtime
        ///    consent prompt — internal field, hence SerializedObject),
        /// 4. FullBody joint set (OVRRuntimeSettings — verified, warns if not).
        /// Over Link, ALSO enable "Developer runtime features" + body tracking
        /// in the Meta Quest Link PC app.</summary>
        [MenuItem("Tools/Robot Teleop/Enable Body Tracking (Project + Scene)", priority = 1)]
        public static void EnableBodyTracking()
        {
            // 1. Build capability.
            var cfg = OVRProjectConfig.CachedProjectConfig;
            if (cfg != null && cfg.bodyTrackingSupport == OVRProjectConfig.FeatureSupport.None)
            {
                cfg.bodyTrackingSupport = OVRProjectConfig.FeatureSupport.Supported;
                OVRProjectConfig.CommitProjectConfig(cfg);
                Debug.Log("[VlaTeleop] OVRProjectConfig: bodyTrackingSupport -> Supported.");
            }

            // 2. Custom AndroidManifest permission (project overrides the
            //    generated manifest, so the entry must exist there).
            const string manifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
            if (System.IO.File.Exists(manifestPath))
            {
                string xml = System.IO.File.ReadAllText(manifestPath);
                if (!xml.Contains("com.oculus.permission.BODY_TRACKING"))
                {
                    xml = xml.Replace("</manifest>",
                        "  <uses-permission android:name=\"com.oculus.permission.BODY_TRACKING\" />\n"
                        + "  <uses-feature android:name=\"com.oculus.software.body_tracking\" "
                        + "android:required=\"false\" />\n</manifest>");
                    System.IO.File.WriteAllText(manifestPath, xml);
                    AssetDatabase.ImportAsset(manifestPath);
                    Debug.Log("[VlaTeleop] AndroidManifest: BODY_TRACKING permission added.");
                }
            }

            // 3. Startup consent prompt on the scene's OVRManager.
            var mgr = Object.FindObjectOfType<OVRManager>();
            if (mgr == null)
            {
                Debug.LogWarning("[VlaTeleop] No OVRManager in scene — body tracking "
                                 + "permission not configured.");
            }
            else
            {
                var so = new SerializedObject(mgr);
                var prop = so.FindProperty("requestBodyTrackingPermissionOnStartup");
                if (prop != null && !prop.boolValue)
                {
                    prop.boolValue = true;
                    so.ApplyModifiedProperties();
                    EditorSceneManager.MarkSceneDirty(mgr.gameObject.scene);
                    Debug.Log("[VlaTeleop] OVRManager: body-tracking permission request "
                              + "on startup ENABLED.");
                }
            }

            // 4. FullBody joint set — legs need it (QuestBodyAnchors.LegsValid).
            var runtime = OVRRuntimeSettings.GetRuntimeSettings();
            if (runtime != null && runtime.BodyTrackingJointSet != OVRPlugin.BodyJointSet.FullBody)
                Debug.LogWarning("[VlaTeleop] OVRRuntimeSettings joint set is not FullBody — "
                                 + "legs will not track. Set it in Project Settings ▸ "
                                 + "Meta XR ▸ Body Tracking, or Movement SDK settings.");
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
