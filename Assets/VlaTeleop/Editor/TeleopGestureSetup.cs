// VLA XR teleop — build the Interaction SDK pose recognizers that drive the
// in-VR transport controls (record / pause / rewind / playback).
//
// This reproduces, in code, the wiring you can inspect in
// Assets/Samples/Meta XR Interaction SDK/…/Example Scenes/PoseExamples.unity:
//
//   Hand (IHand)
//     └─ FingerFeatureStateProvider   per-finger curl/flexion state machine
//          └─ ShapeRecognizerActiveState   compares the live hand against one
//               ▲                          or more ShapeRecognizer assets
//               └── ShapeRecognizer (.asset): ThumbUp, FingersAllOpen, …
//
// PoseExamples then feeds the resulting IActiveState into an
// ActiveStateSelector -> SelectorUnityEventWrapper -> UnityEvents. We consume
// the same bool one step earlier: TeleopGestureCommands reads
// `IActiveState.Active` directly, adds a dwell timer and a world-orientation
// test, and emits a transport command. Reading the bool ourselves (rather
// than going through UnityEvents) is what lets one pose mean different things
// by orientation — thumbs up and thumbs down are the SAME ShapeRecognizer.
//
// Orientation deliberately does NOT use TransformRecognizerActiveState: that
// component needs an IHmd and an ITrackingToWorldTransformer threaded through
// the rig, while TeleopGestureCommands already has metric world landmarks and
// can answer "is the thumb pointing up" exactly. Fewer moving parts, same
// result.
//
// TWO THINGS ARE BOTH CALLED "HAND TRACKING" and they are not the same:
//
//   Building Blocks ▸ Hand Tracking (category *Core*)  -> OVRHand, OVRSkeleton
//       what the teleop stream reads, and all this file's built-in poses need
//   Interaction SDK rig (Quick Actions wizard)         -> Oculus.Interaction
//                                                          .Input.Hand
//       what ShapeRecognizerActiveState reads
//
// A scene can have the first and none of the second — that is the normal
// state of the teleop scenes here, and it is why BuildPoses may report no
// SDK hands in a scene that visibly has hand tracking working. The block that
// used to bridge them ("Hand Interactions") is tagged Hidden + Deprecated, so
// the supported route is GameObject ▸ Interaction SDK ▸ Add OVR Comprehensive
// Interaction Rig — the same rig PoseExamples.unity uses.
//
// Menu:
//   Tools/Robot Teleop/Gestures/Add Gesture Transport     component only
//   Tools/Robot Teleop/Gestures/Build Gesture Poses (ISDK) + recognizers
//   Tools/Robot Teleop/Gestures/Remove Gesture Poses      tear the ISDK part down

using System.Collections.Generic;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class TeleopGestureSetup
    {
        const string PoseRoot = "TeleopGesturePoses";
        const string Pkg = "Packages/com.meta.xr.sdk.interaction/Runtime";
        const string ShapeDir = Pkg + "/Sample/Poses/Shapes";
        const string ThumbThresholds =
            Pkg + "/DefaultSettings/PoseDetection/DefaultThumbFeatureStateThresholds.asset";
        const string FingerThresholds =
            Pkg + "/DefaultSettings/PoseDetection/DefaultFingerFeatureStateThresholds.asset";

        [MenuItem("Tools/Robot Teleop/Gestures/Add Gesture Transport", priority = 3)]
        public static TeleopGestureCommands AddTransport()
        {
            var sender = Object.FindObjectOfType<VlaTeleopSender>();
            if (sender == null)
            {
                EditorUtility.DisplayDialog(
                    "No VLA teleop sender",
                    "Run Tools ▸ Robot Teleop ▸ Add VLA Teleop Sender first — the " +
                    "gesture transport rides in that sender's xr_pose packets.",
                    "OK");
                return null;
            }
            var g = sender.GetComponent<TeleopGestureCommands>();
            if (g == null) g = Undo.AddComponent<TeleopGestureCommands>(sender.gameObject);
            g.sender = sender;
            if (g.bindings == null || g.bindings.Count == 0)
                g.bindings = TeleopGestureCommands.DefaultBindings();

            var panel = Object.FindObjectOfType<VlaTeleopHudPanel>();
            if (panel != null && panel.gestures == null) panel.gestures = g;
            var overlay = Object.FindObjectOfType<RobotOverlayDriver>();
            if (overlay != null && overlay.sender == null) overlay.sender = sender;

            EditorUtility.SetDirty(g);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = g.gameObject;
            Debug.Log("[VlaTeleop] Gesture transport added (built-in landmark " +
                      "poses). Run Gestures ▸ Build Gesture Poses (ISDK) to " +
                      "upgrade to Interaction SDK shape recognizers.");
            return g;
        }

        [MenuItem("Tools/Robot Teleop/Gestures/Build Gesture Poses (ISDK)", priority = 4)]
        public static void BuildPoses()
        {
            var g = Object.FindObjectOfType<TeleopGestureCommands>() ?? AddTransport();
            if (g == null) return;

            // Every ShapeRecognizerActiveState needs a per-hand finger-state
            // provider. The Building Block hands ship an ISDK Hand but not
            // always the pose-detection providers, so make sure both exist.
            if (!EnsureInteractionRig()) return;
            var left = FindHand(Handedness.Left);
            var right = FindHand(Handedness.Right);
            if (left == null && right == null)
            {
                PromptForInteractionRig();
                return;
            }

            var root = GameObject.Find(PoseRoot);
            if (root == null)
            {
                root = new GameObject(PoseRoot);
                Undo.RegisterCreatedObjectUndo(root, "Create " + PoseRoot);
            }

            var providers = new Dictionary<Handedness, FingerFeatureStateProvider>();
            var l = ExistingProvider(Handedness.Left);
            var r = ExistingProvider(Handedness.Right);
            if (l == null && left != null) l = EnsureProvider(left);
            if (r == null && right != null) r = EnsureProvider(right);
            if (l != null) providers[Handedness.Left] = l;
            if (r != null) providers[Handedness.Right] = r;

            // One recognizer object per (binding, hand). A ShapeRecognizer-
            // ActiveState watches ONE hand, so Both/Either bindings get two —
            // reusing a single recognizer for both sides would silently make
            // "both hands" mean "whichever hand that recognizer watches".
            int wired = 0, skipped = 0;
            foreach (var b in g.bindings)
            {
                if (b == null || !b.enabled) continue;
                string[] shapes = ShapesFor(b.fallbackShape);
                if (shapes == null) { skipped++; continue; }
                var recognizers = LoadShapes(shapes);
                if (recognizers == null) { skipped++; continue; }

                bool needLeft = b.hand != TeleopGestureCommands.HandSide.Right;
                bool needRight = b.hand != TeleopGestureCommands.HandSide.Left;
                bool any = false;
                if (needLeft)
                {
                    var s = BuildRecognizer(root, b, Handedness.Left, providers,
                                            recognizers);
                    b.shapeState = s;
                    any |= s != null;
                }
                if (needRight)
                {
                    var s = BuildRecognizer(root, b, Handedness.Right, providers,
                                            recognizers);
                    b.shapeStateRight = s;
                    any |= s != null;
                }
                if (any) wired++; else skipped++;
            }

            EditorUtility.SetDirty(g);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = root;
            Debug.Log($"[VlaTeleop] ISDK pose recognizers: {wired} wired under " +
                      $"'{PoseRoot}'" +
                      (skipped > 0
                          ? $", {skipped} binding(s) left on the built-in landmark "
                            + "test (hand = Either, or no matching SDK shape asset)"
                          : "") +
                      ". Orientation (thumb up vs down, palm facing) stays in " +
                      "TeleopGestureCommands — see its header for why.");
        }

        [MenuItem("Tools/Robot Teleop/Gestures/Remove Gesture Poses", priority = 5)]
        public static void RemovePoses()
        {
            var g = Object.FindObjectOfType<TeleopGestureCommands>();
            if (g != null)
            {
                foreach (var b in g.bindings)
                    if (b != null) b.shapeState = null;
                EditorUtility.SetDirty(g);
            }
            var root = GameObject.Find(PoseRoot);
            if (root != null) Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[VlaTeleop] ISDK pose recognizers removed — the bindings " +
                      "fall back to the built-in landmark tests.");
        }

        /// <summary>Create (or reuse) one ShapeRecognizerActiveState for a
        /// binding on one hand. Returns null when that hand has no provider.</summary>
        static ShapeRecognizerActiveState BuildRecognizer(
            GameObject root, TeleopGestureCommands.Binding b, Handedness h,
            Dictionary<Handedness, FingerFeatureStateProvider> providers,
            ShapeRecognizer[] recognizers)
        {
            if (!providers.TryGetValue(h, out var provider) || provider == null)
                return null;
            string name = $"{b.label}_{h}";
            var go = FindChild(root.transform, name);
            if (go == null)
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create " + name);
                go.transform.SetParent(root.transform, false);
            }
            var state = go.GetComponent<ShapeRecognizerActiveState>();
            if (state == null) state = Undo.AddComponent<ShapeRecognizerActiveState>(go);
            state.InjectAllShapeRecognizerActiveState(provider, recognizers);
            EditorUtility.SetDirty(state);
            return state;
        }

        // ---- one-click setup -------------------------------------------------- //

        const string H1Scene = "Assets/Scenes/H1-Quest.unity";

        /// <summary>Everything in one go: Interaction SDK rig, gesture transport,
        /// finger-state providers, pose recognizers, wiring. Safe to re-run.</summary>
        [MenuItem("Tools/Robot Teleop/Gestures/Set Up ISDK Hand Poses (Active Scene)",
                  priority = 1)]
        public static void SetUpEverything()
        {
            if (Object.FindObjectOfType<OVRCameraRig>() == null)
            {
                EditorUtility.DisplayDialog(
                    "No OVRCameraRig",
                    "The active scene has no OVRCameraRig. Open a teleop scene " +
                    "(Assets/Scenes/H1-Quest.unity) and run this again — or use " +
                    "\"Set Up ISDK Hand Poses in H1-Quest\", which opens it for you.",
                    "OK");
                return;
            }
            if (!EnsureInteractionRig()) return;
            var g = AddTransport();
            if (g == null) return;
            BuildPoses();

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[VlaTeleop] ISDK gesture setup complete for scene " +
                      $"'{scene.name}'. Save the scene to keep it.");
        }

        /// <summary>Reset the binding tables to the shipped defaults and re-wire
        /// the ISDK recognizers to match.
        ///
        /// Needed whenever the defaults change in code: the scene's serialized
        /// lists take precedence, so an existing component silently keeps the
        /// bindings it was created with. Run this after pulling changes if a
        /// gesture or key "should" work and doesn't.</summary>
        [MenuItem("Tools/Robot Teleop/Gestures/Reset Bindings to Defaults", priority = 6)]
        public static void ResetBindings()
        {
            var g = Object.FindObjectOfType<TeleopGestureCommands>();
            if (g == null)
            {
                EditorUtility.DisplayDialog(
                    "No gesture transport",
                    "This scene has no TeleopGestureCommands. Run \"Set Up ISDK " +
                    "Hand Poses (Active Scene)\" first.", "OK");
                return;
            }
            Undo.RecordObject(g, "Reset gesture bindings");
            g.RestoreDefaults();
            EditorUtility.SetDirty(g);
            BuildPoses();                       // re-point shapeState at the recognizers

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
            Debug.Log($"[VlaTeleop] Bindings reset to defaults ({g.bindings.Count} " +
                      $"poses, {g.keyboardShortcuts.Count} keys) and re-wired" +
                      (string.IsNullOrEmpty(scene.path) ? "." : " — scene saved."));
        }

        /// <summary>Same, but opens H1-Quest first and SAVES it.</summary>
        [MenuItem("Tools/Robot Teleop/Gestures/Set Up ISDK Hand Poses in H1-Quest",
                  priority = 2)]
        public static void SetUpH1QuestScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(H1Scene) == null)
            {
                EditorUtility.DisplayDialog("Scene not found",
                                            $"{H1Scene} does not exist.", "OK");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.OpenScene(H1Scene, OpenSceneMode.Single);
            if (!EnsureInteractionRig()) return;
            var g = AddTransport();
            if (g == null) return;
            BuildPoses();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[VlaTeleop] {H1Scene} updated and SAVED — Interaction SDK " +
                      "rig + pose recognizers wired. Press Play (or rebuild) to try " +
                      "the gestures.");
        }

        // ---- the two different "hand tracking" ------------------------------- //

        /// <summary>Menu path of the Interaction SDK's rig wizard — the same rig
        /// PoseExamples.unity uses (prefab OVRComprehensiveInteractionRig).</summary>
        const string RigWizardMenu =
            "GameObject/Interaction SDK/Add OVR Comprehensive Interaction Rig";

        /// <summary>The rig prefab the wizard instantiates. Referenced by GUID
        /// because the wizard class itself is `internal` — we cannot call it,
        /// but we can instantiate exactly what it would have.</summary>
        const string RigPrefabGuid = "0a7d2469f24041c4284c66706f84c45e";
        const string InteractionRigName = "OVRComprehensiveInteractionRig";

        /// <summary>Add the Interaction SDK hand rig to the scene if it has
        /// none, and return whether ISDK hands are present afterwards.
        ///
        /// The rig is a prefab under the OVRCameraRig; `OVRCameraRigRef` needs
        /// an explicit reference to that rig (it asserts on it at Start), and
        /// the Core hand-tracking block's own visuals are disabled so you don't
        /// see two overlapping sets of hands — both things the wizard does.</summary>
        public static bool EnsureInteractionRig()
        {
            if (FindHand(Handedness.Left) != null || FindHand(Handedness.Right) != null)
                return true;

            var camRig = Object.FindObjectOfType<OVRCameraRig>();
            if (camRig == null)
            {
                Debug.LogError("[VlaTeleop] No OVRCameraRig in the scene — cannot add " +
                               "the Interaction SDK rig. Open a teleop scene first.");
                return false;
            }

            string path = AssetDatabase.GUIDToAssetPath(RigPrefabGuid);
            var prefab = string.IsNullOrEmpty(path)
                ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError("[VlaTeleop] Could not find the " + InteractionRigName +
                               " prefab (guid " + RigPrefabGuid + "). Add it by hand " +
                               "via " + RigWizardMenu.Replace("/", " ▸ ") + ".");
                return false;
            }

            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab, camRig.transform);
            rig.name = InteractionRigName;
            rig.transform.localPosition = Vector3.zero;
            rig.transform.localRotation = Quaternion.identity;
            Undo.RegisterCreatedObjectUndo(rig, "Add " + InteractionRigName);

            // OVRCameraRigRef._ovrCameraRig is a serialized field the component
            // asserts on; the prefab ships it empty.
            foreach (var reference in rig.GetComponentsInChildren<OVRCameraRigRef>(true))
            {
                var so = new SerializedObject(reference);
                var prop = so.FindProperty("_ovrCameraRig");
                if (prop != null && prop.objectReferenceValue == null)
                {
                    prop.objectReferenceValue = camRig;
                    so.ApplyModifiedProperties();
                }
            }

            // The Core "Hand Tracking" block renders hands too; leaving both on
            // gives you a doubled, slightly offset pair.
            int hidden = 0;
            foreach (var h in camRig.GetComponentsInChildren<OVRHand>(true))
            {
                if (h.TryGetComponent<OVRSkeletonRenderer>(out var sk) && sk.enabled)
                { sk.enabled = false; hidden++; }
                if (h.TryGetComponent<OVRMeshRenderer>(out var mr) && mr.enabled)
                { mr.enabled = false; hidden++; }
            }

            int hands = Object.FindObjectsOfType<Hand>(true).Length;
            Debug.Log($"[VlaTeleop] Added {InteractionRigName} under " +
                      $"'{camRig.name}' — {hands} Interaction SDK Hand component(s), " +
                      $"{hidden} duplicate Core hand visual(s) disabled. The teleop " +
                      "stream still reads the OVRHand/OVRSkeleton from the Core " +
                      "block; only pose recognition uses these.");
            return hands > 0;
        }

        /// <summary>Explain the Core-vs-Interaction hand-tracking split, which
        /// is genuinely confusing: the Building Blocks window's **Hand
        /// Tracking** block (category *Core*) adds `OVRHand` + `OVRSkeleton`
        /// only — perfect for teleop, and all the built-in poses need — while
        /// the SDK's pose recognizers read `Oculus.Interaction.Input.Hand`,
        /// which comes from the Interaction rig. The block that used to add it
        /// ("Hand Interactions") is tagged Hidden + Deprecated, so it does not
        /// appear in that window at all; the supported route is the Quick
        /// Actions wizard.</summary>
        static void PromptForInteractionRig()
        {
            bool haveOvrHands =
                Object.FindObjectsOfType<OVRHand>(true).Length > 0;

            string msg =
                (haveOvrHands
                    ? "This scene HAS hand tracking — the Building Blocks \"Hand " +
                      "Tracking\" block, category Core. That block adds OVRHand + " +
                      "OVRSkeleton, which is what the teleop stream and the " +
                      "built-in gesture poses use.\n\n"
                    : "This scene has no hand tracking at all.\n\n") +
                "What it does NOT add is Oculus.Interaction.Input.Hand — a " +
                "separate component the Interaction SDK's pose recognizers read " +
                "from. (The Building Block that used to add it, \"Hand " +
                "Interactions\", is tagged Hidden + Deprecated, so you won't " +
                "find it in the Building Blocks window.)\n\n" +
                "The supported way to add it is the Quick Actions wizard:\n" +
                "    " + RigWizardMenu.Replace("/", " ▸ ") + "\n" +
                "which builds the same rig PoseExamples.unity uses. Run it, then " +
                "re-run Build Gesture Poses (ISDK).\n\n" +
                "You do not need any of this to use the transport: every binding " +
                "already has a built-in pose test computed from the metric hand " +
                "landmarks, and those work with the OVRHand you have.";

            if (EditorUtility.DisplayDialog("Interaction SDK hands not in this scene",
                                            msg, "Open the rig wizard", "Keep built-in poses"))
            {
                if (!EditorApplication.ExecuteMenuItem(RigWizardMenu))
                    Debug.LogWarning($"[VlaTeleop] Could not open '{RigWizardMenu}' — " +
                                     "find it by right-clicking in the Hierarchy ▸ " +
                                     "Interaction SDK.");
            }
            else
            {
                Debug.Log("[VlaTeleop] Keeping the built-in landmark poses — the " +
                          "transport works as-is; ISDK recognizers are an optional " +
                          "upgrade.");
            }
        }

        // ---- helpers ---------------------------------------------------------- //

        /// <summary>The SDK sample shapes that reproduce each built-in pose.
        /// null = no equivalent, keep the landmark test.</summary>
        static string[] ShapesFor(TeleopGestureCommands.PoseShape shape)
        {
            switch (shape)
            {
                // Thumb up + every other finger curled. ThumbUp already
                // requires the four fingers to be not-open; adding
                // FingersAllClosed makes it a deliberate fist-with-thumb
                // rather than a loose hand.
                case TeleopGestureCommands.PoseShape.ThumbExtendedFistClosed:
                    return new[] { "ThumbUp", "FingersAllClosed" };
                case TeleopGestureCommands.PoseShape.OpenPalm:
                    return new[] { "FingersAllOpen" };
                case TeleopGestureCommands.PoseShape.PeaceSign:
                    return new[] { "FingersScissors" };
                case TeleopGestureCommands.PoseShape.Fist:
                    return new[] { "FingersAllClosed" };
                default:
                    return null;
            }
        }

        static ShapeRecognizer[] LoadShapes(string[] names)
        {
            var list = new List<ShapeRecognizer>(names.Length);
            foreach (var n in names)
            {
                var asset = AssetDatabase.LoadAssetAtPath<ShapeRecognizer>(
                    $"{ShapeDir}/{n}.asset");
                if (asset == null)
                {
                    Debug.LogWarning($"[VlaTeleop] shape asset '{n}' not found at " +
                                     $"{ShapeDir} — that binding keeps its " +
                                     "built-in landmark test.");
                    return null;
                }
                list.Add(asset);
            }
            return list.ToArray();
        }

        /// <summary>Hand.Handedness reads through the live data source, which
        /// does not exist in edit mode — fall back to the GameObject name,
        /// which the SDK's own rigs spell out ("LeftHand" / "RightHand").
        /// </summary>
        /// <summary>A finger-state provider the RIG already ships, for this
        /// hand. Strongly preferred over adding our own: the comprehensive rig
        /// contains many Hand components — synthetic (visual-only) hands among
        /// them — and its own providers are already bound to the TRACKED hand.
        /// Left to itself, FindHand can return a synthetic hand and we would
        /// hang pose recognition off something that never sees your fingers.</summary>
        static FingerFeatureStateProvider ExistingProvider(Handedness h)
        {
            foreach (var p in Object.FindObjectsOfType<FingerFeatureStateProvider>(true))
            {
                var so = new SerializedObject(p);
                var handProp = so.FindProperty("_hand");
                var hand = handProp?.objectReferenceValue as Component;
                if (hand == null) continue;
                var asHand = hand.GetComponent<Hand>() ?? hand as Hand;
                Handedness found;
                try { found = asHand != null ? asHand.Handedness : NameHandedness(hand.name); }
                catch (System.Exception) { found = NameHandedness(hand.name); }
                if (found != h) continue;
                // Skip synthetic/visual hands — they replay a pose, they do not
                // report your live finger curls.
                if (IsSynthetic(p.gameObject) || IsSynthetic(hand.gameObject)) continue;
                return p;
            }
            return null;
        }

        static bool IsSynthetic(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
                if (t.name.IndexOf("synthetic", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static Hand FindHand(Handedness h)
        {
            foreach (var hand in Object.FindObjectsOfType<Hand>(true))
            {
                Handedness found;
                try
                {
                    found = hand.Handedness;
                }
                catch (System.Exception)
                {
                    found = NameHandedness(hand.name);
                }
                if (found == h) return hand;
            }
            // Second pass by name only, in case Handedness answered but with
            // stale/default data for every hand.
            foreach (var hand in Object.FindObjectsOfType<Hand>(true))
                if (NameHandedness(hand.name) == h) return hand;
            return null;
        }

        static Handedness NameHandedness(string name)
            => name.ToLowerInvariant().Contains("left")
                ? Handedness.Left : Handedness.Right;

        /// <summary>Find (or build) the finger-state provider for a hand. The
        /// five threshold entries are NOT optional — FingerFeatureStateProvider
        /// asserts on exactly one per finger — so a freshly added component is
        /// seeded with the SDK's own defaults, matching the wiring in the SDK's
        /// BaseInteractors prefab (thumb gets its own threshold asset).</summary>
        static FingerFeatureStateProvider EnsureProvider(Hand hand)
        {
            // p.Hand is only resolved at Awake, so in edit mode compare the
            // SERIALIZED reference instead — otherwise every run of this menu
            // would add another provider.
            foreach (var p in Object.FindObjectsOfType<FingerFeatureStateProvider>(true))
            {
                var bound = new SerializedObject(p).FindProperty("_hand");
                if (bound != null && bound.objectReferenceValue == hand) return p;
            }

            var thumb = AssetDatabase.LoadAssetAtPath<FingerFeatureStateThresholds>(
                ThumbThresholds);
            var finger = AssetDatabase.LoadAssetAtPath<FingerFeatureStateThresholds>(
                FingerThresholds);
            if (thumb == null || finger == null)
            {
                Debug.LogError("[VlaTeleop] Interaction SDK default finger-state " +
                               "thresholds not found — cannot build a " +
                               "FingerFeatureStateProvider. Bindings keep the " +
                               "built-in landmark tests.");
                return null;
            }

            var go = new GameObject($"HandFeatures_{hand.Handedness}");
            Undo.RegisterCreatedObjectUndo(go, "Create hand features");
            go.transform.SetParent(hand.transform, false);
            var provider = Undo.AddComponent<FingerFeatureStateProvider>(go);
            provider.InjectAllFingerFeatureStateProvider(
                hand,
                new List<FingerFeatureStateProvider.FingerStateThresholds>
                {
                    Threshold(HandFinger.Thumb, thumb),
                    Threshold(HandFinger.Index, finger),
                    Threshold(HandFinger.Middle, finger),
                    Threshold(HandFinger.Ring, finger),
                    Threshold(HandFinger.Pinky, finger),
                },
                FingerFeatureStateProvider.DefaultFingerShapes,
                false);
            EditorUtility.SetDirty(provider);
            Debug.Log($"[VlaTeleop] added FingerFeatureStateProvider for the " +
                      $"{hand.Handedness} hand (SDK default thresholds).");
            return provider;
        }

        static FingerFeatureStateProvider.FingerStateThresholds Threshold(
            HandFinger finger, FingerFeatureStateThresholds thresholds)
        {
            return new FingerFeatureStateProvider.FingerStateThresholds
            {
                Finger = finger,
                StateThresholds = thresholds,
            };
        }

        static GameObject FindChild(Transform parent, string name)
        {
            foreach (Transform t in parent)
                if (t.name == name) return t.gameObject;
            return null;
        }
    }
}
