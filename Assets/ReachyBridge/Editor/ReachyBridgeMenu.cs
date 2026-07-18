// ReachyBrain XR bridge — Quest port. Editor tooling.
//
// Adds a top-level "ReachyBridge" menu to the Unity menu bar that sets up the
// active scene, builds/deploys to a connected Quest, and helps test. Lives in an
// Editor/ folder so it compiles into the editor-only assembly (never shipped in
// a build) while still seeing the runtime ReachyBridge components and the Meta
// SDK (OVRCameraRig, PassthroughCameraAccess).
//
// Menu:
//   ReachyBridge/Settings…                 dockable window (Mac host, camera toggle, buttons)
//   ReachyBridge/Set Up Active Scene       add + wire the bridge onto a "ReachyBridge" object
//   ReachyBridge/Add Passthrough Camera    add a PassthroughCameraAccess if the scene lacks one
//   ReachyBridge/Build APK                 build an .apk to Builds/
//   ReachyBridge/Build and Run on Quest    build + install + launch on the connected headset
//   ReachyBridge/Install Last Build (adb)  adb install -r the last .apk
//   ReachyBridge/Open README               the setup guide
//   ReachyBridge/Copy Mac Server Command   ./scripts/quest.sh to the clipboard

using System.IO;
using System.Linq;
using Meta.XR;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Proc = System.Diagnostics.Process;

namespace ReachyBridge.EditorTools
{
    public static class ReachyBridgeMenu
    {
        const string HostPref = "ReachyBridge.MacHost";
        const string CameraPref = "ReachyBridge.EnableCamera";
        const string DefaultHost = "192.168.0.217";
        const string ApkName = "ReachyBridge.apk";

        public static string MacHost
        {
            get => EditorPrefs.GetString(HostPref, DefaultHost);
            set => EditorPrefs.SetString(HostPref, value);
        }

        public static bool EnableCamera
        {
            get => EditorPrefs.GetBool(CameraPref, true);
            set => EditorPrefs.SetBool(CameraPref, value);
        }

        static string ApkPath =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds", ApkName);

        // ------------------------------------------------------------------ //
        // Menu items                                                          //
        // ------------------------------------------------------------------ //

        [MenuItem("ReachyBridge/Settings…", priority = 0)]
        static void OpenSettings() => ReachyBridgeSettingsWindow.Open();

        [MenuItem("ReachyBridge/Set Up Active Scene", priority = 20)]
        public static void SetUpActiveScene()
        {
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig == null &&
                !EditorUtility.DisplayDialog(
                    "No OVRCameraRig in the scene",
                    "This scene has no OVRCameraRig, so head pose / hands / passthrough " +
                    "won't work. Start from a passthrough sample scene (CameraViewer or " +
                    "CameraToWorld) and run this again.\n\nAdd the ReachyBridge object anyway?",
                    "Add anyway", "Cancel"))
                return;

            var go = GameObject.Find("ReachyBridge");
            if (go == null)
            {
                go = new GameObject("ReachyBridge");
                Undo.RegisterCreatedObjectUndo(go, "Create ReachyBridge");
            }

            var pointer = GetOrAdd<QuestPointerProvider>(go);
            if (rig != null) pointer.rig = rig;

            var sender = GetOrAdd<ReachyBridgeSender>(go);
            sender.host = MacHost;
            sender.pointer = pointer;
            if (rig != null) sender.rig = rig;

            var controls = GetOrAdd<ReachyBridgeControls>(go);
            controls.sender = sender;

            var hud = GetOrAdd<ReachyBridgeHud>(go);
            hud.sender = sender;
            hud.pointer = pointer;
            if (rig != null) hud.rig = rig;
            controls.hud = hud;

            if (EnableCamera)
            {
                var pub = GetOrAdd<PassthroughVitcPublisher>(go);
                if (pub.cameraAccess == null)
                    pub.cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>();
                if (pub.cameraAccess == null)
                    Debug.LogWarning("[ReachyBridge] No PassthroughCameraAccess in the scene — " +
                                     "walk-capture will do nothing. Run ReachyBridge/Add Passthrough Camera.");
            }
            else
            {
                var pub = go.GetComponent<PassthroughVitcPublisher>();
                if (pub != null) Undo.DestroyObjectImmediate(pub);
            }

            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            Debug.Log($"[ReachyBridge] scene set up — server host {MacHost}:9878, camera publisher {(EnableCamera ? "on" : "off")}");
        }

        [MenuItem("ReachyBridge/Add Passthrough Camera", priority = 21)]
        static void AddPassthroughCamera()
        {
            if (Object.FindFirstObjectByType<PassthroughCameraAccess>() != null)
            {
                Debug.Log("[ReachyBridge] a PassthroughCameraAccess is already in the scene.");
                return;
            }
            var go = new GameObject("PassthroughCamera");
            Undo.RegisterCreatedObjectUndo(go, "Add PassthroughCamera");
            var cam = Undo.AddComponent<PassthroughCameraAccess>(go);
            cam.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log("[ReachyBridge] added PassthroughCameraAccess (Left). Enable passthrough + " +
                      "HEADSET_CAMERA permission (already set in this project).");
        }

        [MenuItem("ReachyBridge/Build APK", priority = 40)]
        static void BuildApk() => Build(run: false);

        [MenuItem("ReachyBridge/Build and Run on Quest", priority = 41)]
        static void BuildAndRun() => Build(run: true);

        [MenuItem("ReachyBridge/Install Last Build (adb)", priority = 42)]
        static void InstallLast()
        {
            if (!File.Exists(ApkPath))
            {
                EditorUtility.DisplayDialog("No APK", $"No build at:\n{ApkPath}\n\nRun Build APK first.", "OK");
                return;
            }
            RunAdb($"install -r \"{ApkPath}\"");
        }

        [MenuItem("ReachyBridge/Open README", priority = 60)]
        static void OpenReadme()
        {
            var path = Path.Combine(Application.dataPath, "ReachyBridge", "README.md");
            if (File.Exists(path)) EditorUtility.OpenWithDefaultApp(path);
            else Debug.LogWarning($"[ReachyBridge] README not found at {path}");
        }

        [MenuItem("ReachyBridge/Copy Mac Server Command", priority = 61)]
        static void CopyServerCommand()
        {
            EditorGUIUtility.systemCopyBuffer = "./scripts/quest.sh";
            Debug.Log("[ReachyBridge] copied './scripts/quest.sh' to the clipboard — run it on the Mac " +
                      $"(same Wi-Fi), then set macHost = {MacHost} on the Quest.");
        }

        // ------------------------------------------------------------------ //
        // Build / deploy helpers                                              //
        // ------------------------------------------------------------------ //

        static void Build(bool run)
        {
            if (!EnsureAndroidTarget()) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scenes = ScenesForBuild();
            if (scenes.Length == 0)
            {
                EditorUtility.DisplayDialog("No scenes", "No active scene and no enabled scenes in " +
                    "Build Settings. Open the scene you set up and try again.", "OK");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = run ? BuildOptions.AutoRunPlayer : BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            if (s.result == BuildResult.Succeeded)
            {
                Debug.Log($"[ReachyBridge] build OK: {ApkPath} ({s.totalSize / (1024 * 1024)} MB)" +
                          (run ? " — installing & launching on the connected Quest." : ""));
                if (!run) EditorUtility.RevealInFinder(ApkPath);
            }
            else
            {
                Debug.LogError($"[ReachyBridge] build {s.result}: {s.totalErrors} error(s).");
                EditorUtility.DisplayDialog("Build failed",
                    $"Result: {s.result}\nErrors: {s.totalErrors}\nSee the Console for details.", "OK");
            }
        }

        static bool EnsureAndroidTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return true;
            if (!EditorUtility.DisplayDialog("Switch to Android?",
                    "The active build target isn't Android. Switch now? (This can take a while.)",
                    "Switch", "Cancel"))
                return false;
            return EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        static string[] ScenesForBuild()
        {
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path)) return new[] { active.path };
            return EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        }

        static void RunAdb(string args)
        {
            var adb = AdbPath();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(adb, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Proc.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                    Debug.Log($"[ReachyBridge] adb {args}\n{outp}");
                else
                    Debug.LogError($"[ReachyBridge] adb {args} failed (exit {p.ExitCode}):\n{err}\n{outp}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ReachyBridge] couldn't run adb ('{adb}'): {e.Message}\n" +
                               "Set the Android SDK in Preferences > External Tools, or put adb on your PATH.");
            }
        }

        static string AdbPath()
        {
            var sdk = EditorPrefs.GetString("AndroidSdkRoot", "");
            if (!string.IsNullOrEmpty(sdk))
            {
                var exe = Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
                var p = Path.Combine(sdk, "platform-tools", exe);
                if (File.Exists(p)) return p;
            }
            return "adb"; // fall back to PATH
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }
    }

    // ---------------------------------------------------------------------- //
    // Settings window                                                         //
    // ---------------------------------------------------------------------- //

    public class ReachyBridgeSettingsWindow : EditorWindow
    {
        public static void Open()
        {
            var w = GetWindow<ReachyBridgeSettingsWindow>(false, "ReachyBridge", true);
            w.minSize = new Vector2(360, 240);
            w.Show();
        }

        void OnGUI()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Reachy XR Bridge — Quest", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Set your Mac's LAN IP (the machine running integrations/viture/server.py), " +
                "set up the active scene, then Build and Run on a connected Quest.",
                MessageType.Info);

            GUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            var host = EditorGUILayout.TextField("Mac host (LAN IP)", ReachyBridgeMenu.MacHost);
            var cam = EditorGUILayout.Toggle("Camera publisher (walk-capture)", ReachyBridgeMenu.EnableCamera);
            if (EditorGUI.EndChangeCheck())
            {
                ReachyBridgeMenu.MacHost = host;
                ReachyBridgeMenu.EnableCamera = cam;
            }

            GUILayout.Space(10);
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Set Up Active Scene")) ReachyBridgeMenu.SetUpActiveScene();
            }

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Build & Deploy", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build APK"))
                    EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("ReachyBridge/Build APK");
                if (GUILayout.Button("Build and Run on Quest"))
                    EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("ReachyBridge/Build and Run on Quest");
            }
            if (GUILayout.Button("Install Last Build (adb)"))
                EditorApplication.delayCall += () => EditorApplication.ExecuteMenuItem("ReachyBridge/Install Last Build (adb)");

            GUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "On the Mac: ./scripts/quest.sh  (same Wi-Fi as the Quest).\n" +
                "Controls: A gaze-capture · B cycle mode · X map-cal · Y robot-locate · " +
                "L-stick walk-capture · R-stick idle · Menu HUD.",
                MessageType.None);
            if (GUILayout.Button("Copy Mac Server Command"))
                EditorApplication.ExecuteMenuItem("ReachyBridge/Copy Mac Server Command");
        }
    }
}
