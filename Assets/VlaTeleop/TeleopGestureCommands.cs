// VLA XR teleop — transport controls for your hands.
//
// Teleoperating the robot uses both hands, so the record button has to BE a
// hand. This component watches for deliberate poses and stamps a transport
// command into the outgoing xr_pose packet; the Python side
// (handtracking/session.py) owns the state machine and answers back over the
// joint-target echo, which is what the HUD's ● REC / timeline reads.
//
//   Thumbs up      start recording — and resume a paused take
//   Open palm      pause (the robot FREEZES; it must not chase you while you
//                  rest your arms or the take gets a jump in it)
//   Thumbs down    stop + save the episode
//   Peace sign     step out of the robot: play the take back while your hands
//                  are ignored (peace sign again = take control back)
//   Pinch + drag   scrub the timeline. Pinch the scrub hand (default LEFT)
//                  and hold; the robot rewinds/fast-forwards WITH your hand,
//                  showing the frame under the cursor. Release to splice the
//                  take there — the frames after the cursor are dropped and
//                  re-recording continues from that point.
//
// POSE RECOGNITION comes from the Interaction SDK's pose system — the pattern
// in Assets/Samples/…/Example Scenes/PoseExamples.unity: a `ShapeRecognizer`
// ScriptableObject describes the required finger states, a
// `ShapeRecognizerActiveState` compares the live hand against it, and the
// result is a plain `IActiveState.Active` bool. Wire those into `shapeState`
// via Tools ▸ Robot Teleop ▸ Gestures ▸ Build Gesture Poses (ISDK) and this
// component becomes a pure state machine over them.
//
// Without that wiring it still works: each binding has a built-in
// `fallbackShape` evaluated from the metric hand landmarks QuestHandLandmarks
// already produces (interior joint angles, same idea as the Python finger
// retargeters). The ISDK path is the better one — it is calibrated,
// hysteretic and editable in the inspector — but a scene with nothing but an
// OVRCameraRig and hand tracking is not left without transport controls.
//
// ORIENTATION (thumb pointing up vs down, palm facing you) is checked here
// rather than with the SDK's TransformRecognizerActiveState: that component
// needs an IHmd + tracking-to-world transformer wired through the rig, while
// the landmarks are already in metric world space and give an exact answer.
// So a "thumbs up" binding is the SDK's ThumbUp shape AND our up-vector test.
//
// Every command is stamped with an incrementing id and REPEATED in each
// packet until the server echoes that id back as `session.ack` (UDP drops
// packets; a lost "stop recording" would be a lost take). Repeats are
// idempotent server-side because the ack is keyed on the id.

using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class TeleopGestureCommands : MonoBehaviour
    {
        /// <summary>Transport commands understood by handtracking/session.py.</summary>
        public enum Transport
        {
            None,
            RecordStart,     // rec_start   (also resumes a paused take)
            RecordPause,     // rec_pause
            RecordResume,    // rec_resume
            RecordToggle,    // rec_toggle
            RecordStop,      // rec_stop    (finalize the episode)
            Play,            // play        (step out and watch)
            PlayStop,        // play_stop
            PlayToggle,      // play_toggle
            NudgeBack,       // nudge -arg  (step the cursor back)
            NudgeForward,    // nudge +arg
            Recenter,        // recenter    (re-anchor the overlay + reset the
                             //              server's torso-yaw zero)
        }

        /// <summary>Built-in pose tests, used when no ISDK shape is wired.</summary>
        public enum PoseShape
        {
            None, ThumbExtendedFistClosed, OpenPalm, PeaceSign, Fist, PointIndex,
        }

        /// <summary>Extra world-orientation requirement layered on the finger
        /// shape. Palm tests are measured against the head's POSITION (the
        /// direction from the hand to your face), not the gaze direction, so
        /// they stay correct when you look away from your own hand.</summary>
        public enum Orientation
        {
            Any,
            ThumbUp,
            ThumbDown,
            /// <summary>Thumb held roughly HORIZONTAL — pointing out to either
            /// side rather than up or down. Distinct from both thumbs-up and
            /// thumbs-down, and easy to hold steady.</summary>
            ThumbSideways,
            FingersUp,
            /// <summary>Palm turned towards your face (you can see your own
            /// palm). Deliberate — an open hand hanging naturally faces down or
            /// away, so this is what separates "pause" from "resting hand".</summary>
            PalmTowardsFace,
            /// <summary>Palm turned away from you — a "stop" sign shown to
            /// someone else.</summary>
            PalmAway,
            /// <summary>Palm turned down at the floor.</summary>
            PalmDown,
            /// <summary>Both palms turned in at each other (a framing gesture).
            /// Only meaningful with HandSide.Both; needs both hands tracked and
            /// reasonably close together, so it is essentially impossible to
            /// strike by accident.</summary>
            PalmsFacingEachOther,
        }

        /// <summary>Which hand(s) must hold the pose. ``Both`` means BOTH
        /// simultaneously — reserved for rare, deliberate actions like
        /// recalibration, where an accidental trigger is expensive.</summary>
        public enum HandSide { Either, Left, Right, Both }

        [Serializable]
        public class Binding
        {
            public bool enabled = true;
            [Tooltip("Shown on the HUD while the pose is being held.")]
            public string label = "";
            public Transport command = Transport.None;
            public HandSide hand = HandSide.Either;

            [Tooltip("Interaction SDK pose recognizer for the LEFT hand " +
                     "(ShapeRecognizerActiveState / ActiveStateGroup — anything " +
                     "implementing IActiveState). Preferred; leave empty to use " +
                     "fallbackShape.")]
            public UnityEngine.Object shapeState;
            [Tooltip("Recognizer for the RIGHT hand. A recognizer is bound to " +
                     "ONE hand, so Both/Either bindings need two; falls back to " +
                     "the left slot when empty.")]
            public UnityEngine.Object shapeStateRight;
            [Tooltip("Built-in finger test used when shapeState is empty.")]
            public PoseShape fallbackShape = PoseShape.None;
            [Tooltip("Extra world-orientation requirement, checked from the " +
                     "metric landmarks (this is what separates thumbs up from " +
                     "thumbs down — the finger shape is identical).")]
            public Orientation orientation = Orientation.Any;

            [Tooltip("Seconds the pose must be held before it fires. Long " +
                     "enough that ordinary manipulation doesn't trigger it.")]
            public float holdSeconds = 0.45f;
            [Tooltip("Argument sent with the command (nudge: seconds).")]
            public float arg = 0f;

            [NonSerialized] public float heldSince = -1f;   // <0 = not posing
            [NonSerialized] public bool latched;            // fired, awaiting release
            [NonSerialized] public float progress;          // 0..1 dwell, for the HUD
        }

        [Header("Sources (auto-found if left empty)")]
        public VlaTeleopSender sender;
        public OVRHand leftHand;
        public OVRHand rightHand;
        public OVRSkeleton leftSkeleton;
        public OVRSkeleton rightSkeleton;
        [Tooltip("Head pose — the scrub drag is measured along the head's right " +
                 "axis so it reads as horizontal no matter which way you face.")]
        public Transform headTransform;

        [Header("Gestures")]
        [Tooltip("Pose -> transport command. Rebind freely; the server only " +
                 "ever sees the command name.")]
        public List<Binding> bindings = new List<Binding>();

        /// <summary>A keyboard shortcut for a transport command. Works in editor
        /// Play mode (including over Link, using the PC's keyboard) — on a
        /// standalone headset build there is no keyboard, so the poses remain
        /// the only control surface there.</summary>
        [Serializable]
        public class KeyBinding
        {
            public bool enabled = true;
            [Tooltip("Key NAME, e.g. r, space, f1. Parsed against whichever " +
                     "input backend the project uses, so it works either way.")]
            public string key = "r";
            public Transport command = Transport.None;
            public float arg = 0f;
            [NonSerialized] public bool wasDown;
        }

        [Header("Keyboard shortcuts (editor / Link)")]
        [Tooltip("Hands-free poses are the real control surface, but a keyboard " +
                 "is unambiguous — useful while calibrating, and as a way out " +
                 "when a pose will not register.")]
        public List<KeyBinding> keyboardShortcuts = DefaultKeys();

        [Header("Timeline scrub (rewind)")]
        public bool scrubEnabled = true;
        public HandSide scrubHand = HandSide.Left;
        [Tooltip("Seconds of index pinch before the scrub engages. Long enough " +
                 "that a quick double pinch (the server's fallback record " +
                 "toggle) never starts a drag.")]
        public float scrubArmSeconds = 0.5f;
        [Tooltip("Hand travel, in meters, that covers the whole take.")]
        public float scrubSpanMeters = 0.5f;
        [Tooltip("Pinch strength that counts as pinching / released.")]
        [Range(0.1f, 1f)] public float pinchOn = 0.85f;
        [Range(0f, 0.9f)] public float pinchOff = 0.4f;

        [Header("Debug")]
        [Tooltip("Require HIGH-confidence tracking for a pose to count. OFF by " +
                 "default — closed-hand poses (fist, thumbs up) report low " +
                 "confidence by their nature, and gating on it makes them " +
                 "silently unfirable. Turn on only if you get false triggers.")]
        public bool requireHighConfidence = false;
        [Tooltip("Seconds between repeated heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;
        [Tooltip("Log every binding's shape/orientation verdict each heartbeat. " +
                 "This is how you tell 'the shape isn't recognized' from 'the " +
                 "shape is fine but the orientation check is rejecting it'.")]
        public bool logBindingDiagnostics = true;

        // ---- read-only state for the HUDs ---------------------------------- //
        public string LastCommandName { get; private set; } = "";
        public float LastCommandAt { get; private set; } = -99f;
        public bool ScrubActive { get; private set; }
        public float ScrubPos { get; private set; }
        /// <summary>Binding currently being held (for the dwell ring), or null.</summary>
        public Binding Posing { get; private set; }

        readonly Vector3[] _left21 = new Vector3[QuestHandLandmarks.Count];
        readonly Vector3[] _right21 = new Vector3[QuestHandLandmarks.Count];
        bool _leftOk, _rightOk;
        bool _leftHighConf, _rightHighConf;

        // scrub drag state
        bool _pinching;
        float _pinchSince = -1f;
        Vector3 _dragOrigin;
        float _dragStartPos;
        int _scrubSession;

        float _nextBeat;
        int _beatCommands;

        void Reset()
        {
            bindings = DefaultBindings();
            keyboardShortcuts = DefaultKeys();
        }

        /// <summary>Stock keyboard mapping. R = recalibrate is the important
        /// one; the rest make the whole transport drivable without poses while
        /// you tune them.</summary>
        public static List<KeyBinding> DefaultKeys()
        {
            return new List<KeyBinding>
            {
                new KeyBinding { key = "r", command = Transport.Recenter },
                new KeyBinding { key = "b", command = Transport.RecordStart },
                new KeyBinding { key = "p", command = Transport.RecordPause },
                new KeyBinding { key = "s", command = Transport.RecordStop },
                new KeyBinding { key = "k", command = Transport.PlayToggle },
            };
        }

        /// <summary>The stock mapping. Also used by the setup menu so the
        /// ISDK build and a bare scene agree on which pose means what.</summary>
        public static List<Binding> DefaultBindings()
        {
            return new List<Binding>
            {
                new Binding
                {
                    label = "REC", command = Transport.RecordStart,
                    hand = HandSide.Right,
                    fallbackShape = PoseShape.ThumbExtendedFistClosed,
                    orientation = Orientation.ThumbUp, holdSeconds = 0.45f,
                },
                // LEFT palm turned in at your own face, held 2.5 s. An open hand
                // is a RESTING pose, so the orientation plus a long dwell is
                // what makes this deliberate; the orientation is measured
                // against your head's POSITION, so looking elsewhere doesn't
                // change the answer.
                new Binding
                {
                    label = "PAUSE", command = Transport.RecordPause,
                    hand = HandSide.Left, fallbackShape = PoseShape.OpenPalm,
                    orientation = Orientation.PalmTowardsFace, holdSeconds = 2.5f,
                },
                new Binding
                {
                    label = "SAVE", command = Transport.RecordStop,
                    hand = HandSide.Right,
                    fallbackShape = PoseShape.ThumbExtendedFistClosed,
                    orientation = Orientation.ThumbDown, holdSeconds = 0.6f,
                },
                new Binding
                {
                    label = "PLAY", command = Transport.PlayToggle,
                    hand = HandSide.Either, fallbackShape = PoseShape.PeaceSign,
                    orientation = Orientation.FingersUp, holdSeconds = 0.5f,
                },
                // LEFT thumbs-DOWN, 1.5 s.
                //
                // Third pose tried for this: two fists and two framing palms
                // both failed to fire in practice. Same thumb-out shape as
                // REC/SAVE, separated only by hand and thumb direction — and
                // both of those live on the RIGHT hand, so nothing collides.
                new Binding
                {
                    label = "RECENTER", command = Transport.Recenter,
                    hand = HandSide.Left,
                    fallbackShape = PoseShape.ThumbExtendedFistClosed,
                    orientation = Orientation.ThumbDown, holdSeconds = 1.5f,
                },
            };
        }

        void Start()
        {
            if (sender == null) sender = FindObjectOfType<VlaTeleopSender>();
            if (sender == null)
            {
                Debug.LogError("[VlaTeleop] TeleopGestureCommands needs a " +
                               "VlaTeleopSender in the scene — no way to send " +
                               "transport commands.");
                enabled = false;
                return;
            }
            if (leftHand == null) leftHand = sender.leftHand;
            if (rightHand == null) rightHand = sender.rightHand;
            if (leftSkeleton == null) leftSkeleton = sender.leftSkeleton;
            if (rightSkeleton == null) rightSkeleton = sender.rightSkeleton;
            if (headTransform == null) headTransform = sender.headTransform;
            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;
            if (bindings == null || bindings.Count == 0)
                bindings = DefaultBindings();

            int isdk = 0;
            foreach (var b in bindings)
                if (AsActiveState(b.shapeState) != null) isdk++;
            Debug.Log($"[VlaTeleop] Gesture transport ready: {bindings.Count} " +
                      $"binding(s), {isdk} using ISDK pose recognizers" +
                      (isdk < bindings.Count
                          ? $", {bindings.Count - isdk} using built-in landmark tests "
                            + "(Tools ▸ Robot Teleop ▸ Gestures ▸ Build Gesture Poses (ISDK) "
                            + "wires the SDK ones)"
                          : "") +
                      $". Scrub: {(scrubEnabled ? scrubHand + " index pinch-and-drag" : "off")}.");
        }

        void Update()
        {
            // Hands are re-read here rather than shared with the sender: the
            // sender only fills them at its send rate (30 Hz), and a dwell
            // timer wants every frame.
            // The confidence gate has to be lifted in BOTH places: TryFill has
            // its own, and relaxing only the one here changes nothing.
            _leftOk = Tracked(leftHand)
                      && QuestHandLandmarks.TryFill(leftSkeleton, _left21,
                                                    requireHighConfidence);
            _rightOk = Tracked(rightHand)
                       && QuestHandLandmarks.TryFill(rightSkeleton, _right21,
                                                     requireHighConfidence);
            _leftHighConf = leftHand != null && leftHand.IsDataHighConfidence;
            _rightHighConf = rightHand != null && rightHand.IsDataHighConfidence;

            EvaluateBindings();
            UpdateKeyboard();
            UpdateScrub();
            Heartbeat();
        }

        void UpdateKeyboard()
        {
            if (keyboardShortcuts == null) return;
            foreach (var k in keyboardShortcuts)
            {
                if (k == null || !k.enabled || k.command == Transport.None) continue;
                bool down = KeyHeld(k.key);
                if (down && !k.wasDown)                       // rising edge
                {
                    Debug.Log($"[VlaTeleop] key '{k.key}' -> {k.command}");
                    FireCommand(k.command, k.arg, k.key.ToUpperInvariant());
                }
                k.wasDown = down;
            }
        }

        /// <summary>Is this key down right now?
        ///
        /// Written against BOTH input backends. This project is set to the new
        /// Input System only (`activeInputHandler: 1`), where the legacy
        /// `UnityEngine.Input` throws InvalidOperationException on first use —
        /// so a legacy-only implementation would not merely fail to work, it
        /// would spam exceptions from Update.</summary>
        static bool KeyHeld(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && Enum.TryParse(name, true, out Key key))
            {
                var ctrl = kb[key];
                if (ctrl != null) return ctrl.isPressed;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Enum.TryParse(name, true, out KeyCode code))
                return Input.GetKey(code);
#endif
            return false;
        }

        /// <summary>Does a key name resolve on the active backend? Editor-side
        /// validation, so a typo is caught at setup instead of by silence.</summary>
        public static bool IsKeyNameValid(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
#if ENABLE_INPUT_SYSTEM
            if (Enum.TryParse(name, true, out Key _)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Enum.TryParse(name, true, out KeyCode _)) return true;
#endif
            return false;
        }

        /// <summary>Is this hand usable for pose evaluation?
        ///
        /// Deliberately does NOT require high confidence by default. Quest
        /// reports low confidence precisely when fingers occlude each other —
        /// which is every closed-hand pose — so gating on it makes those poses
        /// unfirable while looking like the recognizer is broken. The dwell
        /// timer is the noise filter instead; the teleop STREAM still holds on
        /// low-confidence frames (that is a separate, correct check in the
        /// mapper), because a jittery robot arm and a jittery button are not
        /// the same risk.</summary>
        bool Tracked(OVRHand h)
            => h != null && h.IsTracked && h.IsDataValid
               && (!requireHighConfidence || h.IsDataHighConfidence);

        // ---- bindings -------------------------------------------------------- //

        void EvaluateBindings()
        {
            Posing = null;
            float now = Time.unscaledTime;
            foreach (var b in bindings)
            {
                if (b == null || !b.enabled || b.command == Transport.None)
                    continue;
                bool active = Evaluate(b);
                if (!active)
                {
                    b.heldSince = -1f;
                    b.latched = false;       // must release before firing again
                    b.progress = 0f;
                    continue;
                }
                if (b.heldSince < 0f) b.heldSince = now;
                float held = now - b.heldSince;
                b.progress = b.holdSeconds > 0f
                    ? Mathf.Clamp01(held / b.holdSeconds) : 1f;
                if (Posing == null) Posing = b;
                if (!b.latched && held >= b.holdSeconds)
                {
                    b.latched = true;
                    Fire(b);
                }
            }
        }

        bool Evaluate(Binding b)
        {
            if (b.hand == HandSide.Either)
                return EvaluateSide(b, true) || EvaluateSide(b, false);
            if (b.hand == HandSide.Both)
                return EvaluateSide(b, true) && EvaluateSide(b, false);
            return EvaluateSide(b, b.hand == HandSide.Left);
        }

        bool EvaluateSide(Binding b, bool left)
        {
            if (!(left ? _leftOk : _rightOk)) return false;

            // Shape: the ISDK recognizer for THIS hand when wired, else the
            // built-in test. Each recognizer watches one hand, so a Both/Either
            // binding must not reuse one side's recognizer for the other.
            var isdk = AsActiveState(left ? b.shapeState
                                          : (b.shapeStateRight ?? b.shapeState));
            if (isdk != null)
            {
                if (!isdk.Active) return false;
            }
            else if (!ShapeActive(b.fallbackShape, left ? _left21 : _right21))
            {
                return false;
            }
            return OrientationOk(b.orientation, left ? _left21 : _right21, left);
        }

        /// <summary>The ISDK's pose recognizers all expose IActiveState; that
        /// single bool is the whole integration surface (PoseExamples wires
        /// the same bool into ActiveStateSelector -> UnityEvents).</summary>
        static Oculus.Interaction.IActiveState AsActiveState(UnityEngine.Object o)
            => o as Oculus.Interaction.IActiveState;

        void Fire(Binding b)
            => FireCommand(b.command, b.arg,
                           string.IsNullOrEmpty(b.label) ? null : b.label);

        /// <summary>Emit one transport command. Poses and keyboard shortcuts
        /// both come through here, so they cannot drift apart.</summary>
        public void FireCommand(Transport command, float arg, string label = null)
        {
            string wire = WireName(command);
            if (wire == null) return;
            if (command == Transport.NudgeBack)
                arg = -Mathf.Abs(arg <= 0f ? 1f : arg);
            else if (command == Transport.NudgeForward)
                arg = Mathf.Abs(arg <= 0f ? 1f : arg);

            if (sender != null) sender.SendCommand(wire, arg);
            else Debug.LogWarning("[VlaTeleop] no sender — the server will not " +
                                  $"hear '{wire}' (local effects still apply).");
            // Recentering is half local: the server owns the torso-yaw zero, but
            // the ghost/cloud/video anchors live here. Do the local half now so
            // it responds instantly and still works with the server down.
            if (command == Transport.Recenter) RecenterLocal();
            LastCommandName = label ?? wire;
            LastCommandAt = Time.unscaledTime;
            _beatCommands++;
            Debug.Log($"[VlaTeleop] '{LastCommandName}' -> {wire}" +
                      (Mathf.Abs(arg) > 1e-4f ? $" ({arg:0.##})" : ""));
        }

        public static string WireName(Transport c)
        {
            switch (c)
            {
                case Transport.RecordStart: return "rec_start";
                case Transport.RecordPause: return "rec_pause";
                case Transport.RecordResume: return "rec_resume";
                case Transport.RecordToggle: return "rec_toggle";
                case Transport.RecordStop: return "rec_stop";
                case Transport.Play: return "play";
                case Transport.PlayStop: return "play_stop";
                case Transport.PlayToggle: return "play_toggle";
                case Transport.NudgeBack:
                case Transport.NudgeForward: return "nudge";
                case Transport.Recenter: return "recenter";
                default: return null;
            }
        }

        // ---- built-in pose tests (metric landmarks) -------------------------- //
        //
        // MediaPipe topology, as filled by QuestHandLandmarks:
        //   0 wrist | 1-4 thumb CMC/MCP/IP/TIP | 5-8 index | 9-12 middle
        //   13-16 ring | 17-20 pinky  (MCP, PIP, DIP, TIP each)

        const int Wrist = 0, ThumbCmc = 1, ThumbMcp = 2, ThumbIp = 3, ThumbTip = 4;

        static readonly int[] FingerMcp = { 5, 9, 13, 17 };
        static readonly int[] FingerPip = { 6, 10, 14, 18 };
        static readonly int[] FingerTip = { 8, 12, 16, 20 };

        /// <summary>Interior angle at the PIP: a straight finger folds the two
        /// bone vectors back-to-back (dot -> -1), a curled one bends them
        /// towards each other (dot -> 0+). Scale-free, so it needs no
        /// per-person calibration.
        ///
        /// Returns NaN when the joints are coincident — which is what an
        /// un-started or dropped skeleton looks like, every landmark at the
        /// origin. NaN is deliberate: every comparison against it is false, so
        /// BOTH Extended and Curled fail closed. Returning 0 here (as this once
        /// did) reads as "curled" and makes every closed-hand pose fire on a
        /// degenerate hand — which is how Play mode could start recording the
        /// instant you entered it, before the hands were tracked at all.</summary>
        static float Straightness(Vector3[] w, int i)
        {
            Vector3 a = w[FingerMcp[i]] - w[FingerPip[i]];
            Vector3 b = w[FingerTip[i]] - w[FingerPip[i]];
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) return float.NaN;
            return -Vector3.Dot(a.normalized, b.normalized);      // 1 straight, -1 folded
        }

        static bool Extended(Vector3[] w, int i) => Straightness(w, i) > 0.6f;
        static bool Curled(Vector3[] w, int i) => Straightness(w, i) < 0.25f;

        /// <summary>Thumb direction (MCP -> tip), false when degenerate. Unity's
        /// `.normalized` yields ZERO for a zero vector rather than NaN, and a
        /// zero dot passes any "is it horizontal" test — so this has to be
        /// checked explicitly rather than leaned on like Straightness.</summary>
        static bool ThumbAxis(Vector3[] w, out Vector3 axis)
        {
            Vector3 v = w[ThumbTip] - w[ThumbMcp];
            if (v.sqrMagnitude < 1e-8f) { axis = Vector3.zero; return false; }
            axis = v.normalized;
            return true;
        }

        static bool ThumbExtended(Vector3[] w)
        {
            Vector3 a = w[ThumbCmc] - w[ThumbMcp];
            Vector3 b = w[ThumbIp] - w[ThumbMcp];
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) return false;
            return -Vector3.Dot(a.normalized, b.normalized) > 0.55f;
        }

        static bool ShapeActive(PoseShape shape, Vector3[] w)
        {
            switch (shape)
            {
                case PoseShape.ThumbExtendedFistClosed:
                    return ThumbExtended(w) && Curled(w, 0) && Curled(w, 1)
                           && Curled(w, 2) && Curled(w, 3);
                case PoseShape.OpenPalm:
                    return ThumbExtended(w) && Extended(w, 0) && Extended(w, 1)
                           && Extended(w, 2) && Extended(w, 3);
                case PoseShape.PeaceSign:
                    return Extended(w, 0) && Extended(w, 1)
                           && Curled(w, 2) && Curled(w, 3);
                case PoseShape.Fist:
                    return Curled(w, 0) && Curled(w, 1) && Curled(w, 2)
                           && Curled(w, 3);
                case PoseShape.PointIndex:
                    return Extended(w, 0) && Curled(w, 1) && Curled(w, 2)
                           && Curled(w, 3);
                default:
                    return false;    // None: an unbound shape must never fire
            }
        }

        [Header("Orientation thresholds")]
        [Tooltip("How squarely a palm must face its target. 0.5 ≈ within 60°. " +
                 "Raise towards 0.8 if a pose fires when you didn't mean it.")]
        [Range(0.1f, 0.95f)] public float palmDot = 0.6f;
        [Tooltip("PalmsFacingEachOther: hands must be at least this close (m).")]
        public float handsTogetherMeters = 0.5f;
        [Tooltip("ThumbSideways: how close to horizontal the thumb must be. " +
                 "0.45 ≈ within 27° of level. Raise it if the pose is fiddly to " +
                 "hold, lower it if it fires while your hand just rests.")]
        [Range(0.1f, 0.9f)] public float thumbSidewaysDot = 0.45f;

        bool OrientationOk(Orientation o, Vector3[] w, bool left)
        {
            switch (o)
            {
                case Orientation.Any:
                    return true;
                case Orientation.ThumbUp:
                    return ThumbAxis(w, out var tu) && Vector3.Dot(tu, Vector3.up) > 0.5f;
                case Orientation.ThumbDown:
                    return ThumbAxis(w, out var td) && Vector3.Dot(td, Vector3.up) < -0.5f;
                case Orientation.ThumbSideways:
                    return ThumbAxis(w, out var ts)
                           && Mathf.Abs(Vector3.Dot(ts, Vector3.up)) < thumbSidewaysDot;
                case Orientation.FingersUp:
                {
                    Vector3 f = w[FingerTip[0]] - w[FingerMcp[0]];
                    return f.sqrMagnitude > 1e-8f
                           && Vector3.Dot(f.normalized, Vector3.up) > 0.5f;
                }

                // Measured against where your head IS, not where it looks: you
                // glance around constantly while teleoperating, and a gaze-based
                // test turns every relaxed open hand into a pause.
                case Orientation.PalmTowardsFace:
                {
                    Vector3 toHead = ToHead(w);
                    if (toHead == Vector3.zero) return false;     // fail closed
                    return Vector3.Dot(PalmNormal(w, left), toHead) > palmDot;
                }
                case Orientation.PalmAway:
                {
                    Vector3 toHead = ToHead(w);
                    if (toHead == Vector3.zero) return false;
                    return Vector3.Dot(PalmNormal(w, left), toHead) < -palmDot;
                }
                case Orientation.PalmDown:
                    return Vector3.Dot(PalmNormal(w, left), Vector3.down) > palmDot;

                case Orientation.PalmsFacingEachOther:
                {
                    if (!_leftOk || !_rightOk) return false;
                    Vector3 lw = _left21[Wrist], rw = _right21[Wrist];
                    Vector3 sep = rw - lw;
                    if (sep.magnitude > handsTogetherMeters) return false;
                    Vector3 toOther = (left ? sep : -sep).normalized;
                    return Vector3.Dot(PalmNormal(w, left), toOther) > palmDot;
                }
                default:
                    return true;
            }
        }

        /// <summary>Unit vector from the hand towards the head. Returns zero if
        /// there is no head — every palm test then fails closed rather than
        /// comparing against some fixed world axis, which would make an
        /// orientation check look like it "doesn't distinguish anything".</summary>
        Vector3 ToHead(Vector3[] w)
        {
            var head = ResolveHead();
            if (head == null) return Vector3.zero;
            Vector3 d = head.position - w[Wrist];
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.zero;
        }

        bool _warnedNoHead;

        /// <summary>Head transform, resolved lazily.
        ///
        /// Start() order between this component and VlaTeleopSender is not
        /// guaranteed, so binding the head once in Start() can capture the
        /// sender's still-null field and leave every palm test referenceless for
        /// the whole session.</summary>
        Transform ResolveHead()
        {
            if (headTransform != null) return headTransform;
            if (sender != null && sender.headTransform != null)
                return headTransform = sender.headTransform;
            var rig = FindObjectOfType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
                return headTransform = rig.centerEyeAnchor;
            if (Camera.main != null) return headTransform = Camera.main.transform;
            if (!_warnedNoHead)
            {
                _warnedNoHead = true;
                Debug.LogError("[VlaTeleop] No head transform — palm-orientation " +
                               "gestures (PAUSE, RECENTER) cannot be evaluated and " +
                               "will never fire. Assign headTransform explicitly.");
            }
            return null;
        }

        /// <summary>Outward palm normal. The index->pinky knuckle span crossed
        /// with the wrist->middle-knuckle axis gives the palm plane; the cross
        /// order flips with handedness so both hands report "out of the palm".
        /// </summary>
        static Vector3 PalmNormal(Vector3[] w, bool left)
        {
            Vector3 across = w[FingerMcp[3]] - w[FingerMcp[0]];   // index -> pinky
            Vector3 along = w[FingerMcp[1]] - w[Wrist];           // wrist -> middle
            Vector3 n = Vector3.Cross(across, along);
            if (n.sqrMagnitude < 1e-10f) return Vector3.zero;
            n.Normalize();
            return left ? -n : n;
        }

        // ---- timeline scrub --------------------------------------------------- //

        void UpdateScrub()
        {
            if (!scrubEnabled)
            {
                if (ScrubActive) EndScrub();
                return;
            }
            OVRHand hand = scrubHand == HandSide.Right ? rightHand : leftHand;
            Vector3[] w = scrubHand == HandSide.Right ? _right21 : _left21;
            bool ok = scrubHand == HandSide.Right ? _rightOk : _leftOk;
            if (!ok || hand == null)
            {
                if (ScrubActive) EndScrub();
                _pinching = false;
                _pinchSince = -1f;
                return;
            }

            float strength = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            if (!_pinching && strength >= pinchOn)
            {
                _pinching = true;
                _pinchSince = Time.unscaledTime;
            }
            else if (_pinching && strength <= pinchOff)
            {
                _pinching = false;
                _pinchSince = -1f;
                if (ScrubActive) EndScrub();
                return;
            }
            if (!_pinching) return;

            // Held long enough to mean it? Then the hand becomes the playhead.
            if (!ScrubActive)
            {
                if (Time.unscaledTime - _pinchSince < scrubArmSeconds) return;
                ScrubActive = true;
                _scrubSession++;
                _dragOrigin = w[Wrist];
                _dragStartPos = ScrubPos;       // continue from the last cursor
                Debug.Log("[VlaTeleop] scrub armed — drag to rewind, release to splice.");
            }
            Vector3 right = headTransform != null ? headTransform.right : Vector3.right;
            float dx = Vector3.Dot(w[Wrist] - _dragOrigin, right);
            ScrubPos = Mathf.Clamp01(_dragStartPos + dx / Mathf.Max(0.05f, scrubSpanMeters));
            sender.SetScrub(_scrubSession, true, ScrubPos);
        }

        void EndScrub()
        {
            ScrubActive = false;
            // The release is what commits the splice server-side; it is
            // repeated in following packets so a dropped datagram can't leave
            // the session stuck in scrub.
            sender.SetScrub(_scrubSession, false, ScrubPos);
            Debug.Log($"[VlaTeleop] scrub released at {ScrubPos:0.00} (splice).");
        }

        void Heartbeat()
        {
            if (heartbeatSeconds <= 0f || Time.unscaledTime < _nextBeat) return;
            _nextBeat = Time.unscaledTime + heartbeatSeconds;
            Debug.Log($"[VlaTeleop] gestures hb hands L:{Mark(_leftOk, _leftHighConf)} " +
                      $"R:{Mark(_rightOk, _rightHighConf)} fired:{_beatCommands} " +
                      $"scrub:{(ScrubActive ? ScrubPos.ToString("0.00") : "–")} " +
                      $"last:{(LastCommandName.Length > 0 ? LastCommandName : "none")}");
            _beatCommands = 0;
            if (logBindingDiagnostics) LogBindingDiagnostics();
        }

        static string Mark(bool ok, bool high) => !ok ? "–" : (high ? "✓" : "~");

        /// <summary>Per-binding verdict: does the SHAPE match, and separately,
        /// does the ORIENTATION allow it? Those two failing look identical from
        /// inside the headset ("nothing happens") but need opposite fixes.</summary>
        void LogBindingDiagnostics()
        {
            var sb = new System.Text.StringBuilder("[VlaTeleop] poses ");
            foreach (var b in bindings)
            {
                if (b == null || !b.enabled || b.command == Transport.None) continue;
                sb.Append(b.label).Append(':');
                sb.Append(Verdict(b, true)).Append('/').Append(Verdict(b, false));
                sb.Append("  ");
            }
            sb.Append("(L/R; . untracked, s shape-only, S shape+orientation)");
            Debug.Log(sb.ToString());
        }

        char Verdict(Binding b, bool left)
        {
            if (!(left ? _leftOk : _rightOk)) return '.';
            var w = left ? _left21 : _right21;
            var isdk = AsActiveState(b.shapeState);
            bool shape = isdk != null ? isdk.Active : ShapeActive(b.fallbackShape, w);
            if (!shape) return '-';
            return OrientationOk(b.orientation, w, left) ? 'S' : 's';
        }

        // ---- inspector conveniences ------------------------------------------- //

        /// <summary>Reset BOTH tables to the shipped defaults.
        ///
        /// Serialized lists in a saved scene win over the code defaults, so a
        /// component placed before a binding changed keeps the old one forever.
        /// Anything that ships new defaults has to reset here or they never
        /// reach an existing scene.</summary>
        [ContextMenu("Restore Default Bindings")]
        public void RestoreDefaults()
        {
            bindings = DefaultBindings();
            keyboardShortcuts = DefaultKeys();
        }

        /// <summary>Re-anchor everything that hangs off the player: the ghost's
        /// captured root, the point cloud's parked anchor and the video panel.
        /// The server side of the same command resets its torso-yaw zero.</summary>
        [ContextMenu("Recenter Now")]
        public void RecenterLocal()
        {
            var driver = FindObjectOfType<RobotOverlayDriver>();
            if (driver != null)
            {
                driver.Recenter();               // also re-parks cloud + panel
                return;
            }
            // No ghost in the scene — still re-park whatever overlays exist.
            var cloud = FindObjectOfType<RobotPointCloudOverlay>();
            if (cloud != null) cloud.PlaceAnchorInFront();
            var cam = FindObjectOfType<RobotCameraOverlay>();
            if (cam != null) cam.Recenter();
        }

        /// <summary>Headless entry: run one binding against synthetic hands,
        /// through the REAL shape + orientation path. Pass null for a hand that
        /// should read as untracked. Used by VLAPlaybackOverlayHeadlessTest.</summary>
        public bool EvaluatePoseForTest(Binding b, Vector3[] left21, Vector3[] right21,
                                        Transform head)
        {
            headTransform = head;
            _warnedNoHead = true;                 // harness drives this deliberately
            _leftOk = left21 != null;
            _rightOk = right21 != null;
            if (_leftOk) Array.Copy(left21, _left21, QuestHandLandmarks.Count);
            if (_rightOk) Array.Copy(right21, _right21, QuestHandLandmarks.Count);
            return Evaluate(b);
        }

        [ContextMenu("Test: Start Recording")]
        void TestRecord() => sender?.SendCommand("rec_start", 0f);

        [ContextMenu("Test: Stop Recording")]
        void TestStop() => sender?.SendCommand("rec_stop", 0f);

        [ContextMenu("Test: Toggle Playback")]
        void TestPlay() => sender?.SendCommand("play_toggle", 0f);
    }
}
