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
        }

        /// <summary>Built-in pose tests, used when no ISDK shape is wired.</summary>
        public enum PoseShape
        {
            None, ThumbExtendedFistClosed, OpenPalm, PeaceSign, Fist, PointIndex,
        }

        public enum Orientation { Any, ThumbUp, ThumbDown, PalmForward, FingersUp }

        public enum HandSide { Either, Left, Right }

        [Serializable]
        public class Binding
        {
            public bool enabled = true;
            [Tooltip("Shown on the HUD while the pose is being held.")]
            public string label = "";
            public Transport command = Transport.None;
            public HandSide hand = HandSide.Either;

            [Tooltip("Interaction SDK pose recognizer (ShapeRecognizerActiveState / " +
                     "ActiveStateGroup — anything implementing IActiveState). " +
                     "Preferred; leave empty to use fallbackShape.")]
            public UnityEngine.Object shapeState;
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
        [Tooltip("Seconds between repeated heartbeat log lines (0 = off).")]
        public float heartbeatSeconds = 5f;

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
                new Binding
                {
                    label = "PAUSE", command = Transport.RecordPause,
                    hand = HandSide.Left, fallbackShape = PoseShape.OpenPalm,
                    orientation = Orientation.PalmForward, holdSeconds = 0.5f,
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
            _leftOk = Tracked(leftHand) && QuestHandLandmarks.TryFill(leftSkeleton, _left21);
            _rightOk = Tracked(rightHand) && QuestHandLandmarks.TryFill(rightSkeleton, _right21);

            EvaluateBindings();
            UpdateScrub();
            Heartbeat();
        }

        static bool Tracked(OVRHand h)
            => h != null && h.IsTracked && h.IsDataValid && h.IsDataHighConfidence;

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
            return EvaluateSide(b, b.hand == HandSide.Left);
        }

        bool EvaluateSide(Binding b, bool left)
        {
            if (!(left ? _leftOk : _rightOk)) return false;

            // Shape: the ISDK recognizer when wired, else the built-in test.
            var isdk = AsActiveState(b.shapeState);
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
        {
            string wire = WireName(b.command);
            if (wire == null) return;
            float arg = b.command == Transport.NudgeBack
                ? -Mathf.Abs(b.arg <= 0f ? 1f : b.arg)
                : b.command == Transport.NudgeForward
                    ? Mathf.Abs(b.arg <= 0f ? 1f : b.arg)
                    : b.arg;
            sender.SendCommand(wire, arg);
            LastCommandName = string.IsNullOrEmpty(b.label) ? wire : b.label;
            LastCommandAt = Time.unscaledTime;
            _beatCommands++;
            Debug.Log($"[VlaTeleop] gesture '{LastCommandName}' -> {wire}" +
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
        /// per-person calibration.</summary>
        static float Straightness(Vector3[] w, int i)
        {
            Vector3 a = w[FingerMcp[i]] - w[FingerPip[i]];
            Vector3 b = w[FingerTip[i]] - w[FingerPip[i]];
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) return 0f;
            return -Vector3.Dot(a.normalized, b.normalized);      // 1 straight, -1 folded
        }

        static bool Extended(Vector3[] w, int i) => Straightness(w, i) > 0.6f;
        static bool Curled(Vector3[] w, int i) => Straightness(w, i) < 0.25f;

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

        bool OrientationOk(Orientation o, Vector3[] w, bool left)
        {
            switch (o)
            {
                case Orientation.Any:
                    return true;
                case Orientation.ThumbUp:
                    return Vector3.Dot((w[ThumbTip] - w[ThumbMcp]).normalized,
                                       Vector3.up) > 0.5f;
                case Orientation.ThumbDown:
                    return Vector3.Dot((w[ThumbTip] - w[ThumbMcp]).normalized,
                                       Vector3.up) < -0.5f;
                case Orientation.FingersUp:
                    return Vector3.Dot((w[FingerTip[0]] - w[FingerMcp[0]]).normalized,
                                       Vector3.up) > 0.5f;
                case Orientation.PalmForward:
                    return Vector3.Dot(PalmNormal(w, left), ViewDirection()) > 0.4f;
                default:
                    return true;
            }
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

        Vector3 ViewDirection()
            => headTransform != null ? headTransform.forward : Vector3.forward;

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
            Debug.Log($"[VlaTeleop] gestures hb hands L:{(_leftOk ? "✓" : "–")} " +
                      $"R:{(_rightOk ? "✓" : "–")} fired:{_beatCommands} " +
                      $"scrub:{(ScrubActive ? ScrubPos.ToString("0.00") : "–")} " +
                      $"last:{(LastCommandName.Length > 0 ? LastCommandName : "none")}");
            _beatCommands = 0;
        }

        // ---- inspector conveniences ------------------------------------------- //

        [ContextMenu("Restore Default Bindings")]
        public void RestoreDefaults() => bindings = DefaultBindings();

        [ContextMenu("Test: Start Recording")]
        void TestRecord() => sender?.SendCommand("rec_start", 0f);

        [ContextMenu("Test: Stop Recording")]
        void TestStop() => sender?.SendCommand("rec_stop", 0f);

        [ContextMenu("Test: Toggle Playback")]
        void TestPlay() => sender?.SendCommand("play_toggle", 0f);
    }
}
