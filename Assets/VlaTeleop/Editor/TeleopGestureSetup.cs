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
            if (left != null) providers[Handedness.Left] = EnsureProvider(left);
            if (right != null) providers[Handedness.Right] = EnsureProvider(right);

            // One recognizer object per (binding, hand). A binding set to
            // "Either" hand would need two recognizers and only one slot, so
            // those keep the built-in landmark test — it is hand-agnostic by
            // construction and costs nothing.
            int wired = 0, skipped = 0;
            foreach (var b in g.bindings)
            {
                if (b == null || !b.enabled) continue;
                string[] shapes = ShapesFor(b.fallbackShape);
                if (shapes == null)
                {
                    skipped++;
                    continue;
                }
                if (b.hand == TeleopGestureCommands.HandSide.Either)
                {
                    skipped++;
                    continue;
                }
                var h = b.hand == TeleopGestureCommands.HandSide.Left
                    ? Handedness.Left : Handedness.Right;
                if (!providers.TryGetValue(h, out var provider) || provider == null)
                {
                    skipped++;
                    continue;
                }
                var recognizers = LoadShapes(shapes);
                if (recognizers == null)
                {
                    skipped++;
                    continue;
                }
                string name = $"{b.label}_{h}";
                var go = FindChild(root.transform, name);
                if (go == null)
                {
                    go = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(go, "Create " + name);
                    go.transform.SetParent(root.transform, false);
                }
                var state = go.GetComponent<ShapeRecognizerActiveState>();
                if (state == null)
                    state = Undo.AddComponent<ShapeRecognizerActiveState>(go);
                state.InjectAllShapeRecognizerActiveState(provider, recognizers);
                EditorUtility.SetDirty(state);
                b.shapeState = state;
                wired++;
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

        // ---- the two different "hand tracking" ------------------------------- //

        /// <summary>Menu path of the Interaction SDK's rig wizard — the same rig
        /// PoseExamples.unity uses (prefab OVRComprehensiveInteractionRig).</summary>
        const string RigWizardMenu =
            "GameObject/Interaction SDK/Add OVR Comprehensive Interaction Rig";

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
