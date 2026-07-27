// Headless (Unity CLI) proof of the one-click Interaction SDK gesture setup.
//
// Builds a scene shaped like the teleop scenes — an OVRCameraRig with Core
// hand-tracking (OVRHand + OVRSkeleton, NO Oculus.Interaction.Input.Hand) —
// then runs the real menu action and checks what it produced:
//
//   * the OVRComprehensiveInteractionRig prefab is instantiated under the rig
//     (the same prefab PoseExamples.unity uses; its wizard class is `internal`,
//     so the tool instantiates the prefab by GUID instead)
//   * OVRCameraRigRef._ovrCameraRig is wired — the component asserts on it at
//     Start, so an unwired rig throws the moment you press Play
//   * duplicate Core hand visuals are disabled (otherwise two offset hands)
//   * every ISDK Hand gets a FingerFeatureStateProvider with the SDK's own 5
//     threshold entries (thumb has its own asset)
//   * each binding gets ShapeRecognizerActiveState(s) wired to the right hand's
//     provider — and Both/Either bindings get TWO, since a recognizer watches
//     one hand
//   * re-running is idempotent (no duplicated rigs or recognizer objects)
//
//   unity: -executeMethod VlaTeleop.EditorTools.VLAGestureRigHeadlessTest.Run

using System;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class VLAGestureRigHeadlessTest
    {
        static readonly List<string> Failures = new List<string>();
        static int _checks;

        public static void Run()
        {
            try
            {
                RunInner();
                Debug.Log(Failures.Count == 0
                    ? "GESTURERIG|RESULT pass"
                    : $"GESTURERIG|RESULT fail ({Failures.Count})");
                foreach (var f in Failures) Debug.Log($"GESTURERIG|FAIL {f}");
                EditorApplication.Exit(Failures.Count == 0 ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VLAGestureRig] FAILED: {e}");
                EditorApplication.Exit(1);
            }
        }

        static void Check(bool ok, string what, string detail)
        {
            _checks++;
            Debug.Log($"GESTURERIG|CHECK {(ok ? "ok  " : "FAIL")} {what}: {detail}");
            if (!ok) Failures.Add($"{what}: {detail}");
        }

        static void RunInner()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A stand-in for the teleop scenes: OVRCameraRig + the Core
            // hand-tracking block's OVRHand/OVRSkeleton, and nothing from the
            // Interaction SDK.
            var rigGo = new GameObject("OVRCameraRig");
            var camRig = rigGo.AddComponent<OVRCameraRig>();
            var tracking = new GameObject("TrackingSpace").transform;
            tracking.SetParent(rigGo.transform, false);
            foreach (var side in new[] { "Left", "Right" })
            {
                var anchor = new GameObject($"{side}HandAnchor");
                anchor.transform.SetParent(tracking, false);
                var hand = new GameObject($"[BuildingBlock] Hand Tracking {side.ToLower()}");
                hand.transform.SetParent(anchor.transform, false);
                hand.AddComponent<OVRHand>();
                hand.AddComponent<OVRSkeleton>();
                hand.AddComponent<OVRMesh>();
                hand.AddComponent<OVRMeshRenderer>();
                hand.AddComponent<OVRSkeletonRenderer>();
            }
            var sender = new GameObject("RobotTeleop").AddComponent<VlaTeleopSender>();
            sender.rig = camRig;

            Check(UnityEngine.Object.FindObjectsOfType<Hand>(true).Length == 0,
                  "scene starts with no Interaction SDK hands",
                  "Core hand-tracking only — the state the teleop scenes are in");

            // ---- the actual menu action ------------------------------------- //
            TeleopGestureSetup.SetUpEverything();

            var hands = UnityEngine.Object.FindObjectsOfType<Hand>(true);
            Check(hands.Length >= 2, "Interaction SDK hands installed",
                  $"{hands.Length} Hand component(s) after setup");

            var rig = GameObject.Find("OVRComprehensiveInteractionRig");
            Check(rig != null, "comprehensive rig prefab instantiated",
                  rig != null
                      ? $"under '{rig.transform.parent?.name}'"
                      : "NOT FOUND — the prefab GUID may have changed");

            var refs = UnityEngine.Object.FindObjectsOfType<OVRCameraRigRef>(true);
            int wiredRefs = refs.Count(r =>
            {
                var so = new SerializedObject(r);
                var p = so.FindProperty("_ovrCameraRig");
                return p != null && p.objectReferenceValue != null;
            });
            Check(refs.Length > 0 && wiredRefs == refs.Length,
                  "OVRCameraRigRef is wired to the camera rig",
                  $"{wiredRefs}/{refs.Length} wired (it asserts on this at Start)");

            int liveVisuals = camRig.GetComponentsInChildren<OVRSkeletonRenderer>(true)
                                    .Count(r => r.enabled)
                              + camRig.GetComponentsInChildren<OVRMeshRenderer>(true)
                                      .Count(r => r.enabled);
            Check(liveVisuals == 0, "duplicate Core hand visuals disabled",
                  $"{liveVisuals} Core hand renderer(s) still enabled");

            // ---- providers --------------------------------------------------- //
            var providers = UnityEngine.Object.FindObjectsOfType<FingerFeatureStateProvider>(true);
            Check(providers.Length >= 2, "finger-state providers present",
                  $"{providers.Length} FingerFeatureStateProvider(s)");
            foreach (var p in providers)
            {
                var so = new SerializedObject(p);
                var thresholds = so.FindProperty("_fingerStateThresholds");
                int n = thresholds != null && thresholds.isArray ? thresholds.arraySize : -1;
                Check(n == 5, $"provider on '{p.gameObject.name}' has 5 thresholds",
                      $"{n} entries (thumb needs its own asset; the SDK asserts on 5)");
                var handProp = so.FindProperty("_hand");
                Check(handProp != null && handProp.objectReferenceValue != null,
                      $"provider on '{p.gameObject.name}' is bound to a hand",
                      handProp?.objectReferenceValue != null
                          ? handProp.objectReferenceValue.name : "UNBOUND");
            }

            // The recognizers must hang off a TRACKED hand. The comprehensive
            // rig also contains synthetic (visual-only) hands, which replay a
            // pose rather than reporting your live finger curls — wiring pose
            // detection to one of those yields a recognizer that never fires.
            foreach (var s in UnityEngine.Object
                         .FindObjectsOfType<ShapeRecognizerActiveState>(true))
            {
                var so = new SerializedObject(s);
                var prov = so.FindProperty("_fingerFeatureStateProvider")
                             ?.objectReferenceValue as Component;
                bool synthetic = prov != null && IsUnderSynthetic(prov.transform);
                Check(prov != null && !synthetic,
                      $"'{s.gameObject.name}' reads a tracked (non-synthetic) hand",
                      prov == null ? "no provider"
                                   : $"provider '{prov.gameObject.name}'" +
                                     (synthetic ? " is SYNTHETIC" : ""));
            }

            // ---- bindings ----------------------------------------------------- //
            var g = UnityEngine.Object.FindObjectOfType<TeleopGestureCommands>();
            Check(g != null, "gesture transport component added",
                  g != null ? $"{g.bindings.Count} bindings" : "MISSING");
            if (g == null) return;

            foreach (var b in g.bindings)
            {
                if (b == null || !b.enabled) continue;
                bool needLeft = b.hand != TeleopGestureCommands.HandSide.Right;
                bool needRight = b.hand != TeleopGestureCommands.HandSide.Left;
                var l = b.shapeState as ShapeRecognizerActiveState;
                var r = b.shapeStateRight as ShapeRecognizerActiveState;

                if (needLeft)
                    Check(l != null, $"'{b.label}' has a LEFT recognizer",
                          l != null ? l.gameObject.name : "null");
                if (needRight)
                    Check(r != null, $"'{b.label}' has a RIGHT recognizer",
                          r != null ? r.gameObject.name : "null");
                if (b.hand == TeleopGestureCommands.HandSide.Both ||
                    b.hand == TeleopGestureCommands.HandSide.Either)
                    Check(l != null && r != null && l != r,
                          $"'{b.label}' ({b.hand}) uses TWO distinct recognizers",
                          l != null && r != null
                              ? (l == r ? "SAME object for both hands" : "distinct")
                              : "missing one");

                foreach (var s in new[] { l, r })
                {
                    if (s == null) continue;
                    Check(s.Shapes != null && s.Shapes.Count > 0,
                          $"'{s.gameObject.name}' has shape assets",
                          s.Shapes != null ? $"{s.Shapes.Count} ShapeRecognizer(s)" : "none");
                }
            }

            // ---- idempotence --------------------------------------------------- //
            int rigsBefore = CountByName("OVRComprehensiveInteractionRig");
            int posesBefore = UnityEngine.Object
                .FindObjectsOfType<ShapeRecognizerActiveState>(true).Length;
            TeleopGestureSetup.SetUpEverything();
            int rigsAfter = CountByName("OVRComprehensiveInteractionRig");
            int posesAfter = UnityEngine.Object
                .FindObjectsOfType<ShapeRecognizerActiveState>(true).Length;
            Check(rigsAfter == rigsBefore && posesAfter == posesBefore,
                  "re-running the setup is idempotent",
                  $"rigs {rigsBefore}->{rigsAfter}, recognizers {posesBefore}->{posesAfter}");

            Debug.Log($"GESTURERIG|checks {_checks}");
        }

        static bool IsUnderSynthetic(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name.IndexOf("synthetic", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static int CountByName(string name)
            => Resources.FindObjectsOfTypeAll<GameObject>()
                        .Count(o => o.name == name && o.scene.IsValid());
    }
}
