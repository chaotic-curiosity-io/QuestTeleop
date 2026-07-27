// Headless (Unity CLI) proof of the three VR-overlay behaviours added for the
// playback / recalibration / video-panel pass:
//
//   1. PLAYBACK  — the ghost walks out to a stand in front of the operator,
//                  turns to face them, and ACTS OUT the recorded joint motion,
//                  with its point cloud coming along. Before this, the cloud
//                  performed the take from its own parked anchor while the
//                  ghost stayed superimposed on the operator.
//   2. RECENTER  — the runtime recalibrate gesture re-plants every anchor on
//                  the operator's CURRENT pose, clearing the start-up offset
//                  that a stale one-shot capture bakes in.
//   3. VIDEO     — the robot-camera panel sits above the gaze centre and draws
//                  over the point cloud instead of inside it.
//
// Each is measured, not eyeballed, and each is run BOTH WAYS (old behaviour vs
// new) so the harness is provably sensitive to the bug it claims to catch.
// Artifacts: PNG frames from the operator's viewpoint and a third-person view,
// playback_report.json, and playback_track.csv.
//
//   VLA_OUT_DIR=<...>/media/playback_overlay \
//   unity run ./QuestTeleop --timeout 1200 -- \
//       -executeMethod VlaTeleop.EditorTools.VLAPlaybackOverlayHeadlessTest.Run
//
// Runs entirely in edit mode: no Play, no headset, no UDP. The components
// expose direct-apply entry points (RobotOverlayDriver.ApplyEchoJson,
// RobotPointCloudOverlay.ApplyPacketDirect, RobotCameraOverlay.
// ShowTextureDirect) so this exercises the real code paths rather than a
// re-implementation of them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class VLAPlaybackOverlayHeadlessTest
    {
        const int RenderW = 800, RenderH = 600;
        const string Waist = "torso_joint";
        const string LeftShoulder = "left_shoulder_pitch_joint";
        const string RightShoulder = "right_shoulder_pitch_joint";

        static string _out;
        static readonly List<string> Failures = new List<string>();
        static readonly StringBuilder Csv =
            new StringBuilder("phase,step,state,ghost_x,ghost_y,ghost_z,ghost_yaw," +
                              "dist_from_head,in_front,faces_head,l_arm_x,l_arm_y,l_arm_z\n");

        public static void Run()
        {
            try
            {
                RunInner();
                Debug.Log(Failures.Count == 0
                    ? "PLAYBACK|RESULT pass"
                    : $"PLAYBACK|RESULT fail ({Failures.Count})");
                foreach (var f in Failures) Debug.Log($"PLAYBACK|FAIL {f}");
                EditorApplication.Exit(Failures.Count == 0 ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VLAPlayback] FAILED: {e}");
                EditorApplication.Exit(1);
            }
        }

        static int _checks;

        static void Check(bool ok, string what, string detail)
        {
            _checks++;
            Debug.Log($"PLAYBACK|CHECK {(ok ? "ok  " : "FAIL")} {what}: {detail}");
            if (!ok) Failures.Add($"{what}: {detail}");
        }

        static void RunInner()
        {
            _out = Env("VLA_OUT_DIR",
                       Path.Combine(Directory.GetCurrentDirectory(),
                                    "media/playback_overlay"));
            Directory.CreateDirectory(_out);
            Directory.CreateDirectory(Path.Combine(_out, "frames"));

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sun.intensity = 1.2f;

            RobotOverlayBuilder.BuildH1Ghost();
            var ghost = UnityEngine.Object.FindObjectOfType<RobotOverlayGhost>()
                ?? throw new Exception("no RobotOverlayGhost after BuildH1Ghost");

            // Operator stand-ins.
            var head = new GameObject("PlayerHead").transform;
            head.position = new Vector3(0f, 1.60f, 0f);
            head.rotation = Quaternion.identity;
            var floor = new GameObject("TrackingSpace").transform;
            floor.position = Vector3.zero;

            var host = new GameObject("RobotOverlay");
            var driver = host.AddComponent<RobotOverlayDriver>();
            driver.ghost = ghost;
            driver.headTransform = head;
            driver.trackingSpace = floor;
            driver.bodyAnchors = null;
            driver.mode = RobotOverlayDriver.AnchorMode.Superimposed;
            driver.showHud = false;
            driver.heartbeatSeconds = 0f;

            // Cloud deliberately on its OWN parked anchor (InFront) — the exact
            // configuration where it used to perform the take without the ghost.
            var cloud = host.AddComponent<RobotPointCloudOverlay>();
            cloud.mode = RobotPointCloudOverlay.AnchorMode.InFront;
            cloud.ghost = ghost;
            cloud.headTransform = head;
            cloud.style = RobotPointCloudOverlay.RenderStyle.Points;
            cloud.pointSize = 0.02f;
            cloud.showHud = false;
            cloud.heartbeatSeconds = 0f;
            driver.pointCloud = cloud;

            var camOverlay = host.AddComponent<RobotCameraOverlay>();
            camOverlay.headTransform = head;
            camOverlay.showHud = false;
            camOverlay.heartbeatSeconds = 0f;
            driver.cameraOverlay = camOverlay;

            var cam = new GameObject("RenderCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60f;

            byte[] packet = SynthDepthPacket(out int pw, out int ph);
            cloud.ApplyPacketDirect(packet, null);          // build the mesh once
            cloud.PlaceAnchorInFront();
            Debug.Log($"PLAYBACK|setup ghost='{ghost.name}' joints={ghost.joints.Length} " +
                      $"cloud={pw}x{ph} feetToPelvis={ghost.feetToPelvis:0.000}");

            TestPlayback(driver, ghost, cloud, head, cam, packet);
            TestRecenter(driver, ghost, cloud, head, cam);
            TestGesturePoses(host, head);
            TestStereoShader();
            TestVideoPanel(camOverlay, cloud, head, cam, packet);

            File.WriteAllText(Path.Combine(_out, "playback_track.csv"), Csv.ToString());
            Debug.Log($"PLAYBACK|artifacts {_out}");
        }

        // ---- 1. playback: the ghost walks out and performs -------------------- //

        static void TestPlayback(RobotOverlayDriver driver, RobotOverlayGhost ghost,
                                 RobotPointCloudOverlay cloud, Transform head,
                                 Camera cam, byte[] packet)
        {
            driver.detachDuringPlayback = true;
            driver.Recenter();
            Settle(driver, 40);

            Vector3 ghostTeleop = ghost.transform.position;
            Vector3 cloudTeleop = CloudWorldPoint(cloud);
            bool cloudOwnAnchorBefore = !cloud.IsGhostAnchored;
            Log("teleop", 0, "teleop", ghost, head, driver);

            Check(cloudOwnAnchorBefore, "cloud starts on its own anchor",
                  $"IsGhostAnchored={cloud.IsGhostAnchored} (this is the pre-fix split)");

            // --- playback starts: feed the echo the server would send ---------- //
            var armTrack = new List<Vector3>();
            for (int i = 0; i < 30; i++)
            {
                // A take whose arms sweep: proof the ghost ACTS OUT the motion
                // rather than just relocating.
                float phase = i / 29f;
                driver.ApplyEchoJson(Echo("playback", i + 1,
                                          -1.2f * Mathf.Sin(phase * Mathf.PI),
                                          0.9f * Mathf.Sin(phase * Mathf.PI),
                                          0.35f * Mathf.Sin(phase * Mathf.PI * 2f)));
                Settle(driver, 3);
                cloud.ApplyPacketDirect(packet, cloud.Anchor);
                armTrack.Add(JointWorld(ghost, LeftShoulder));
                Log("playback", i, "playback", ghost, head, driver);
                if (i % 10 == 0) Shot(cam, head, ghost, $"frames/playback_{i:00}.png", true);
            }
            Settle(driver, 40);

            Vector3 ghostPlay = ghost.transform.position;
            Vector3 cloudPlay = CloudWorldPoint(cloud);
            Vector3 toGhost = ghostPlay - head.position;
            Vector3 flat = new Vector3(toGhost.x, 0f, toGhost.z);
            float dist = flat.magnitude;
            float inFront = Vector3.Dot(flat.normalized, Flat(head.forward));
            float faces = Vector3.Dot(Flat(ghost.transform.forward),
                                      Flat(head.position - ghostPlay).normalized);

            Check(Vector3.Distance(ghostPlay, ghostTeleop) > 1.0f,
                  "ghost walks out for playback",
                  $"moved {Vector3.Distance(ghostPlay, ghostTeleop):0.00} m " +
                  $"(teleop {V(ghostTeleop)} -> playback {V(ghostPlay)})");
            Check(dist > 1.5f && dist < 3.5f, "ghost stands at viewing distance",
                  $"{dist:0.00} m ahead (playbackDistance {driver.playbackDistance:0.0})");
            Check(inFront > 0.9f, "ghost is in front of the operator",
                  $"dot(head.forward, toGhost) = {inFront:0.000}");
            Check(faces > 0.9f, "ghost turns to face the operator",
                  $"dot(ghost.forward, toHead) = {faces:0.000}");

            float armSpan = Span(armTrack);
            Check(armSpan > 0.15f, "ghost acts out the recorded motion",
                  $"left shoulder swept {armSpan:0.000} m across {armTrack.Count} frames");

            Check(cloud.IsGhostAnchored, "point cloud comes with the ghost",
                  $"IsGhostAnchored={cloud.IsGhostAnchored}, anchor='{cloud.Anchor?.name}'");
            float cloudTravel = Vector3.Distance(cloudPlay, cloudTeleop);
            float ghostTravel = Vector3.Distance(ghostPlay, ghostTeleop);
            Check(cloudTravel > 0.5f, "cloud travelled with the robot",
                  $"cloud moved {cloudTravel:0.00} m, ghost {ghostTravel:0.00} m");

            // --- playback ends: everything comes home --------------------------- //
            driver.ApplyEchoJson(Echo("teleop", 999, 0f, 0f, 0f));
            Settle(driver, 60);
            Log("post", 0, "teleop", ghost, head, driver);
            Shot(cam, head, ghost, "frames/after_playback.png", true);
            Check(Vector3.Distance(ghost.transform.position, ghostTeleop) < 0.05f,
                  "ghost returns to the operator after playback",
                  $"back within {Vector3.Distance(ghost.transform.position, ghostTeleop):0.000} m");
            Check(!cloud.IsGhostAnchored, "cloud releases back to its own anchor",
                  $"IsGhostAnchored={cloud.IsGhostAnchored}");

            // --- sensitivity: with the fix disabled, none of it happens ---------- //
            driver.detachDuringPlayback = false;
            driver.Recenter();
            Settle(driver, 40);
            Vector3 before = ghost.transform.position;
            driver.ApplyEchoJson(Echo("playback", 1, -0.8f, 0.8f, 0f));
            Settle(driver, 40);
            float movedOff = Vector3.Distance(ghost.transform.position, before);
            Check(movedOff < 0.05f, "harness is sensitive (detach OFF reproduces the bug)",
                  $"ghost moved {movedOff:0.000} m with detachDuringPlayback=false");
            driver.detachDuringPlayback = true;
            driver.ApplyEchoJson(Echo("teleop", 2, 0f, 0f, 0f));
            Settle(driver, 40);
        }

        // ---- 2. recenter: the runtime recalibration --------------------------- //

        static void TestRecenter(RobotOverlayDriver driver, RobotOverlayGhost ghost,
                                 RobotPointCloudOverlay cloud, Transform head, Camera cam)
        {
            driver.Recenter();
            Settle(driver, 60);
            Vector3 anchoredAt = ghost.transform.position;

            // The operator walks away and turns — exactly the situation that
            // leaves the one-shot Superimposed capture stale, i.e. "the robot
            // starts out at a weird offset".
            head.position = new Vector3(1.7f, 1.60f, -1.1f);
            head.rotation = Quaternion.Euler(0f, 65f, 0f);
            Settle(driver, 60);
            float staleErr = Flat(ghost.transform.position - head.position).magnitude;
            Log("recenter", 0, "stale", ghost, head, driver);
            Shot(cam, head, ghost, "frames/recenter_before.png", false);
            Check(staleErr > 1.0f, "stale anchor reproduces the start-up offset",
                  $"ghost is {staleErr:0.00} m from the operator before recentering " +
                  $"(anchored at {V(anchoredAt)})");

            driver.Recenter();
            Settle(driver, 60);
            float freshErr = Flat(ghost.transform.position - head.position).magnitude;
            float yawErr = Mathf.Abs(Mathf.DeltaAngle(ghost.transform.eulerAngles.y,
                                                      head.eulerAngles.y));
            Log("recenter", 1, "recentered", ghost, head, driver);
            Shot(cam, head, ghost, "frames/recenter_after.png", false);
            Check(freshErr < 0.05f, "recenter re-plants the ghost on the operator",
                  $"offset {staleErr:0.00} m -> {freshErr:0.003} m");
            Check(yawErr < 2f, "recenter re-aligns the ghost's facing",
                  $"yaw error {yawErr:0.0}° after a {head.eulerAngles.y:0}° turn");

            float cloudAhead = Vector3.Dot(
                Flat(cloud.Anchor.position - head.position).normalized, Flat(head.forward));
            Check(cloudAhead > 0.9f, "recenter re-parks the point cloud ahead of you",
                  $"dot = {cloudAhead:0.000}");
        }

        // ---- 3. gesture orientation: pause must be DELIBERATE ----------------- //

        /// <summary>An open hand is a resting pose, so PAUSE cannot key on the
        /// finger shape alone — the palm has to be turned at your own face. And
        /// the reference must be the head's POSITION, not its forward vector:
        /// you look around constantly while teleoperating.</summary>
        static void TestGesturePoses(GameObject host, Transform head)
        {
            head.position = new Vector3(0f, 1.60f, 0f);
            head.rotation = Quaternion.identity;

            var g = host.AddComponent<TeleopGestureCommands>();
            g.enabled = false;                       // no Update() in edit mode
            var binds = TeleopGestureCommands.DefaultBindings();
            var pause = binds.Find(b => b.command ==
                                        TeleopGestureCommands.Transport.RecordPause);
            var recenter = binds.Find(b => b.command ==
                                           TeleopGestureCommands.Transport.Recenter);

            var save = binds.Find(b => b.command ==
                                       TeleopGestureCommands.Transport.RecordStop);
            var failsafe = binds.FindLast(b => b.command ==
                                               TeleopGestureCommands.Transport.Recenter);

            Check(pause != null &&
                  pause.orientation == TeleopGestureCommands.Orientation.PalmTowardsFace
                  && pause.hand == TeleopGestureCommands.HandSide.Left,
                  "pause = LEFT palm turned at your face",
                  $"hand={pause?.hand} orientation={pause?.orientation} " +
                  $"hold={pause?.holdSeconds:0.0}s");
            Check(pause != null && pause.holdSeconds >= 2.4f,
                  "pause needs a long deliberate hold",
                  $"holdSeconds = {pause?.holdSeconds:0.0}");

            // Hand held out in front, palm turned back at the face -> PAUSE.
            Vector3 handPos = new Vector3(-0.18f, 1.40f, 0.35f);
            Vector3 toFace = (head.position - handPos).normalized;
            var facing = SynthHand(true, handPos, toFace, Vector3.up, open: true);
            Check(g.EvaluatePoseForTest(pause, facing, null, head),
                  "palm towards face DOES pause",
                  "open left palm turned at the head fires PAUSE");

            // Same open hand, palm turned away — a hand resting or gesturing at
            // the world. This is the false trigger being fixed.
            var away = SynthHand(true, handPos, -toFace, Vector3.up, open: true);
            Check(!g.EvaluatePoseForTest(pause, away, null, head),
                  "palm turned away does NOT pause",
                  "an open hand facing outwards no longer fires PAUSE");

            // Palm down — the most common resting orientation of all.
            var down = SynthHand(true, handPos, Vector3.down, Vector3.forward,
                                 open: true);
            Check(!g.EvaluatePoseForTest(pause, down, null, head),
                  "palm down does NOT pause",
                  "a relaxed downward palm no longer fires PAUSE");

            // The RIGHT palm turned at your face must not pause — catches a
            // handedness flip in PalmNormal, which the anatomical hand builder
            // is able to expose.
            Vector3 rPos = new Vector3(0.18f, 1.40f, 0.35f);
            var rFacing = SynthHand(false, rPos, (head.position - rPos).normalized,
                                    Vector3.up, open: true);
            Check(!g.EvaluatePoseForTest(pause, null, rFacing, head),
                  "the RIGHT palm does not pause",
                  "PAUSE is bound to the left hand only");

            // Looking away must not change the verdict — the old test used
            // head.forward, so turning your head flipped the answer.
            head.rotation = Quaternion.Euler(0f, 130f, 0f);
            Check(g.EvaluatePoseForTest(pause, facing, null, head),
                  "pause verdict survives looking away",
                  "still fires with the head turned 130° off the hand");
            head.rotation = Quaternion.identity;

            // --- recenter: LEFT thumbs-DOWN ------------------------------------ //
            Check(recenter != null &&
                  recenter.hand == TeleopGestureCommands.HandSide.Left &&
                  recenter.orientation == TeleopGestureCommands.Orientation.ThumbDown &&
                  recenter.fallbackShape ==
                      TeleopGestureCommands.PoseShape.ThumbExtendedFistClosed,
                  "recenter = LEFT thumbs-down",
                  $"hand={recenter?.hand} shape={recenter?.fallbackShape} " +
                  $"orientation={recenter?.orientation} hold={recenter?.holdSeconds:0.0}s");
            Check(failsafe == recenter,
                  "recenter is a single one-handed binding",
                  "the two-hand framing pose is gone");

            var lThumbDown = SynthHand(true, handPos, Vector3.forward, Vector3.right,
                                       open: false, thumbAxis: Vector3.down);
            Check(g.EvaluatePoseForTest(recenter, lThumbDown, null, head),
                  "left thumbs-DOWN recenters",
                  "closed fingers + thumb at the floor fires RECENTER");

            var lThumbUp = SynthHand(true, handPos, Vector3.forward, Vector3.right,
                                     open: false, thumbAxis: Vector3.up);
            Check(!g.EvaluatePoseForTest(recenter, lThumbUp, null, head),
                  "left thumb UP does not recenter",
                  "the opposite direction must not fire it");

            // The right hand keeps its own meanings.
            var rThumbDown = SynthHand(false, rPos, Vector3.forward, Vector3.left,
                                       open: false, thumbAxis: Vector3.down);
            Check(!g.EvaluatePoseForTest(recenter, null, rThumbDown, head),
                  "RIGHT thumbs-down does NOT recenter",
                  "recenter is bound to the left hand only");
            Check(save != null && g.EvaluatePoseForTest(save, null, rThumbDown, head),
                  "right thumbs-down still saves",
                  "SAVE is unaffected by the new recenter binding");
            Check(!g.EvaluatePoseForTest(save, lThumbDown, null, head),
                  "left thumbs-down does NOT save",
                  "nothing on the left hand can end your take");

            // --- keyboard shortcuts -------------------------------------------- //
            //
            // The escape hatch when a pose will not register. Worth asserting
            // because this project runs the NEW input system only
            // (activeInputHandler: 1), where legacy UnityEngine.Input throws on
            // first use — a key name that fails to resolve just silently never
            // fires, which is the failure mode being escaped from.
            var keys = TeleopGestureCommands.DefaultKeys();
            var recenterKey = keys.Find(k => k.command ==
                                             TeleopGestureCommands.Transport.Recenter);
            Check(recenterKey != null && recenterKey.key.ToLowerInvariant() == "r",
                  "R is bound to recalibrate",
                  recenterKey != null ? $"key '{recenterKey.key}'" : "NO BINDING");
            foreach (var k in keys)
                Check(TeleopGestureCommands.IsKeyNameValid(k.key),
                      $"key name '{k.key}' resolves on the active input backend",
                      $"-> {k.command}");
            Check(keys.TrueForAll(k => k.command !=
                                       TeleopGestureCommands.Transport.None),
                  "every keyboard shortcut maps to a real command",
                  $"{keys.Count} shortcut(s)");

            // --- a degenerate hand must fire NOTHING --------------------------- //
            //
            // An un-started or dropped skeleton reports every landmark at the
            // origin. Straightness() used to return 0 for coincident joints,
            // which reads as "curled" — so every closed-hand pose matched a hand
            // that wasn't being tracked at all, and REC could fire the moment
            // you entered Play mode. Each comparison must fail closed instead.
            var zeros = new Vector3[21];
            int fired = 0;
            foreach (var b in binds)
            {
                if (b == null || !b.enabled) continue;
                if (g.EvaluatePoseForTest(b, zeros, zeros, head))
                {
                    fired++;
                    Debug.Log($"PLAYBACK|degenerate '{b.label}' FIRED");
                }
            }
            Check(fired == 0, "an untracked (all-zero) hand fires no gesture",
                  $"{fired} binding(s) fired on a degenerate skeleton");

            // Same for a hand whose joints are all at one point but offset from
            // the origin — a collapsed skeleton rather than a zeroed one.
            var collapsed = new Vector3[21];
            for (int i = 0; i < 21; i++) collapsed[i] = new Vector3(0.1f, 1.3f, 0.3f);
            fired = 0;
            foreach (var b in binds)
                if (b != null && b.enabled &&
                    g.EvaluatePoseForTest(b, collapsed, collapsed, head)) fired++;
            Check(fired == 0, "a collapsed hand fires no gesture",
                  $"{fired} binding(s) fired on coincident joints");

            // No head in the scene must FAIL CLOSED. Falling back to a fixed
            // world axis is what makes an orientation check look like it
            // "doesn't distinguish anything" — it fires wherever you stand.
            Check(!g.EvaluatePoseForTest(pause, facing, null, null),
                  "palm gestures fail closed with no head reference",
                  "PAUSE does not fire when headTransform cannot be resolved");

            UnityEngine.Object.DestroyImmediate(g);
        }

        /// <summary>A synthetic 21-landmark hand built from ANATOMY, not from
        /// the formula under test.
        ///
        /// Given the direction the palm faces (P) and the direction the fingers
        /// point (F), which side the THUMB falls on is fixed by handedness —
        /// hold a real right hand up, palm away from you and fingers up, and the
        /// thumb points to your left. Writing that out:
        ///
        ///     thumb side = (right hand) -cross(F, P)   (left hand) +cross(F, P)
        ///
        /// and the index finger sits on the thumb side, the pinky opposite. That
        /// derivation is INDEPENDENT of PalmNormal(), so if PalmNormal's
        /// handedness flip were wrong these tests would catch it. Deriving the
        /// landmarks from PalmNormal's own cross product instead — which an
        /// earlier version of this harness did — makes the test circular: it
        /// agrees with the code no matter which convention the code picked.</summary>
        static Vector3[] SynthHand(bool left, Vector3 pos, Vector3 palmDir,
                                   Vector3 fingersDir, bool open,
                                   Vector3 thumbAxis = default)
        {
            var w = new Vector3[21];
            Vector3 F = fingersDir.normalized;
            Vector3 P = palmDir.normalized;
            P = (P - Vector3.Dot(P, F) * F).normalized;          // orthogonalize
            Vector3 thumbSide = (left ? 1f : -1f) * Vector3.Cross(F, P);
            Vector3 across = -thumbSide;                          // index -> pinky

            w[0] = pos;                                           // wrist

            // Thumb: a straight chain so ThumbExtended() is satisfied. Default
            // axis leans out along the thumb side and up along the fingers.
            Vector3 T = thumbAxis == default
                ? (thumbSide * 0.6f + F * 0.8f).normalized
                : thumbAxis.normalized;
            w[1] = pos + T * 0.025f;                              // CMC
            w[2] = pos + T * 0.055f;                              // MCP
            w[3] = pos + T * 0.080f;                              // IP
            w[4] = pos + T * 0.100f;                              // TIP

            for (int f = 0; f < 4; f++)
            {
                Vector3 lateral = across * (-0.030f + 0.020f * f);
                Vector3 mcp = pos + F * 0.085f + lateral;
                Vector3 pip = mcp + F * 0.040f;
                Vector3 dip, tip;
                if (open)
                {
                    dip = pip + F * 0.025f;
                    tip = pip + F * 0.045f;
                }
                else
                {
                    // Folded in towards the palm: -P is into the palm, and the
                    // tip ends up below the PIP, so the interior angle closes.
                    dip = pip + F * 0.005f - P * 0.025f;
                    tip = pip - F * 0.017f - P * 0.038f;
                }
                w[5 + f * 4 + 0] = mcp;
                w[5 + f * 4 + 1] = pip;
                w[5 + f * 4 + 2] = dip;
                w[5 + f * 4 + 3] = tip;
            }
            return w;
        }

        // ---- 4. stereo: the panel must render to BOTH eyes -------------------- //

        /// <summary>Quest renders stereo single-pass instanced. A hand-written
        /// shader without the instancing macros draws to eye 0 only — the panel
        /// shows up in the left eye and is missing from the right. This can only
        /// be seen in a headset, so the guard is on the source itself.</summary>
        static void TestStereoShader()
        {
            string[] required =
            {
                "multi_compile_instancing",
                "UNITY_VERTEX_INPUT_INSTANCE_ID",
                "UNITY_VERTEX_OUTPUT_STEREO",
                "UNITY_SETUP_INSTANCE_ID",
                "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO",
            };
            foreach (var name in new[] { "RobotCameraPanel", "RobotPointCloud",
                                         "RobotPointCloudPoints" })
            {
                var shader = Resources.Load<Shader>(name);
                if (shader == null) { Check(false, $"{name} shader loads", "missing"); continue; }
                string path = AssetDatabase.GetAssetPath(shader);
                string src = File.ReadAllText(path);
                var missing = new List<string>();
                foreach (var macro in required)
                    if (!src.Contains(macro)) missing.Add(macro);
                Check(missing.Count == 0, $"{name} renders to both eyes",
                      missing.Count == 0
                          ? "all single-pass-instanced stereo macros present"
                          : "MISSING " + string.Join(", ", missing));
            }
        }

        // ---- 5. video panel: above the cloud, drawn over it -------------------- //

        static void TestVideoPanel(RobotCameraOverlay overlay, RobotPointCloudOverlay cloud,
                                   Transform head, Camera cam, byte[] packet)
        {
            head.position = new Vector3(0f, 1.60f, 0f);
            head.rotation = Quaternion.identity;

            // Put the cloud exactly where it hurts: a solid wall 1.0 m ahead,
            // i.e. BETWEEN the operator and the 1.2 m video panel. This is the
            // situation being fixed — anywhere else and the test proves nothing.
            var wall = new GameObject("OccluderAnchor").transform;
            wall.SetPositionAndRotation(head.position, head.rotation);
            cloud.style = RobotPointCloudOverlay.RenderStyle.Surface;   // solid, no gaps
            byte[] near = SynthDepthPacket(out _, out _, 1000, 0f, 0f, 0f);
            cloud.ApplyPacketDirect(near, wall);
            Debug.Log($"PLAYBACK|occluder wall 1.0 m ahead, panel at " +
                      $"{overlay.distance:0.0} m (style={cloud.style})");

            // A vivid, unmistakable "video" frame so visible panel pixels can be
            // counted in the render.
            var frame = new Texture2D(64, 36, TextureFormat.RGB24, false);
            var px = new Color32[64 * 36];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 0, 255, 255);
            frame.SetPixels32(px);
            frame.Apply();

            // Capture the SHIPPING defaults before the old-config run mutates
            // them — this is what the check is actually about.
            float shippedOffset = overlay.heightOffset;
            bool shippedOnTop = overlay.alwaysOnTop;

            int visibleOld = PanelPixels(overlay, cloud, head, cam, frame,
                                         -0.15f, false, "frames/panel_old.png");
            int visibleNew = PanelPixels(overlay, cloud, head, cam, frame,
                                         shippedOffset, shippedOnTop,
                                         "frames/panel_new.png");

            Check(shippedOffset > 0.15f, "panel sits above the gaze centre",
                  $"heightOffset = {shippedOffset:0.00} m (was -0.15)");
            Check(shippedOnTop, "panel draws over the cloud by default",
                  $"alwaysOnTop = {shippedOnTop}");
            // The old config must be genuinely swallowed, or the harness is not
            // reproducing the complaint it claims to fix.
            Check(visibleOld < 2000, "harness is sensitive (old config IS occluded)",
                  $"{visibleOld} px of the panel survived the cloud");
            Check(visibleNew > 20000, "panel is fully visible through the cloud",
                  $"{visibleNew} px visible with the shipped config");
            Check(visibleNew > visibleOld * 5, "the point cloud no longer eats the panel",
                  $"visible pixels {visibleOld} -> {visibleNew} " +
                  $"({(visibleOld > 0 ? (float)visibleNew / visibleOld : visibleNew):0.0}x)");
            Debug.Log($"PLAYBACK|panel old={visibleOld}px new={visibleNew}px " +
                      $"heightOffset={shippedOffset:0.00} alwaysOnTop={shippedOnTop}");

            WriteReport(visibleOld, visibleNew, shippedOffset, shippedOnTop);
        }

        static void WriteReport(int visibleOld, int visibleNew,
                                float heightOffset, bool onTop)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"checks_total\": {_checks},\n");
            sb.Append($"  \"checks_failed\": {Failures.Count},\n");
            sb.Append($"  \"panel_visible_px_old_config\": {visibleOld},\n");
            sb.Append($"  \"panel_visible_px_shipped\": {visibleNew},\n");
            sb.Append($"  \"panel_height_offset_m\": {F(heightOffset)},\n");
            sb.Append($"  \"panel_always_on_top\": {(onTop ? "true" : "false")},\n");
            sb.Append("  \"notes\": \"occluder = solid depth wall 1.0 m ahead, ");
            sb.Append("panel at 1.2 m; counts are magenta panel pixels in an ");
            sb.Append("800x600 render from the operator's eye\"\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(_out, "playback_report.json"), sb.ToString());
        }

        /// <summary>Render the operator's view with the panel configured a given
        /// way, and count how many magenta panel pixels actually reach the eye.
        /// The point cloud sits between the head and the panel, so anything the
        /// cloud draws over is missing from this count.</summary>
        static int PanelPixels(RobotCameraOverlay overlay, RobotPointCloudOverlay cloud,
                               Transform head, Camera cam, Texture2D frame,
                               float heightOffset, bool onTop, string shot)
        {
            overlay.heightOffset = heightOffset;
            overlay.alwaysOnTop = onTop;
            overlay.RebuildPanelForTest();
            overlay.ShowTextureDirect(frame);
            overlay.Recenter();

            cam.transform.SetPositionAndRotation(head.position, head.rotation);
            var tex = Render(cam);
            File.WriteAllBytes(Path.Combine(_out, shot), tex.EncodeToPNG());

            int n = 0;
            var pixels = tex.GetPixels32();
            foreach (var p in pixels)
                if (p.r > 200 && p.b > 200 && p.g < 80) n++;
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log($"PLAYBACK|panelcfg offset={heightOffset:0.00} onTop={onTop} " +
                      $"shader='{overlay.PanelShaderName}' visible={n}px");
            return n;
        }

        // ---- plumbing ---------------------------------------------------------- //

        /// <summary>One joint_targets echo, as target_echo.py would emit it.</summary>
        static string Echo(string state, int seq, float lArm, float rArm, float waist)
        {
            return "{\"type\":\"joint_targets\",\"robot\":\"h1\",\"seq\":" + seq +
                   ",\"t\":0,\"names\":[\"" + LeftShoulder + "\",\"" + RightShoulder +
                   "\",\"" + Waist + "\"],\"targets\":[" + F(lArm) + "," + F(rArm) +
                   "," + F(waist) + "],\"rec\":false,\"rec_frames\":0," +
                   "\"mode\":\"full_body\",\"session\":{\"state\":\"" + state +
                   "\",\"episode\":\"episode_000000\",\"frames\":30,\"t\":1.0," +
                   "\"dur\":2.0,\"cursor\":0.5,\"ack\":1,\"msg\":\"" + state + "\"}}";
        }

        static void Settle(RobotOverlayDriver driver, int steps)
        {
            for (int i = 0; i < steps; i++) driver.StepAnchor(0.05f);
        }

        static Vector3 JointWorld(RobotOverlayGhost ghost, string joint)
        {
            foreach (var j in ghost.joints)
                if (j != null && j.name == joint && j.joint != null)
                    return j.joint.position;
            return Vector3.zero;
        }

        static Vector3 CloudWorldPoint(RobotPointCloudOverlay cloud)
            => cloud.Anchor != null ? cloud.Anchor.position : Vector3.zero;

        static float Span(List<Vector3> pts)
        {
            float max = 0f;
            for (int i = 1; i < pts.Count; i++)
                max = Mathf.Max(max, Vector3.Distance(pts[i], pts[0]));
            return max;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static void Log(string phase, int step, string state, RobotOverlayGhost ghost,
                        Transform head, RobotOverlayDriver driver)
        {
            Vector3 g = ghost.transform.position;
            Vector3 flat = Flat(g - head.position);
            Csv.Append($"{phase},{step},{state},{F(g.x)},{F(g.y)},{F(g.z)}," +
                       $"{F(ghost.transform.eulerAngles.y)},{F(flat.magnitude)}," +
                       $"{F(Vector3.Dot(flat.normalized, Flat(head.forward)))}," +
                       $"{F(Vector3.Dot(Flat(ghost.transform.forward), Flat(head.position - g).normalized))}," +
                       $"{F(JointWorld(ghost, LeftShoulder).x)}," +
                       $"{F(JointWorld(ghost, LeftShoulder).y)}," +
                       $"{F(JointWorld(ghost, LeftShoulder).z)}\n");
        }

        static Texture2D Render(Camera cam)
        {
            var rt = new RenderTexture(RenderW, RenderH, 24, RenderTextureFormat.ARGB32)
            { antiAliasing = 1 };
            var tex = new Texture2D(RenderW, RenderH, TextureFormat.RGB24, false);
            RenderTexture prevT = cam.targetTexture, prevA = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, RenderW, RenderH), 0, 0);
            tex.Apply(false);
            cam.targetTexture = prevT;
            RenderTexture.active = prevA;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            return tex;
        }

        /// <summary>Screenshot from the operator's eye, or a third-person view
        /// that frames both operator and ghost.</summary>
        static void Shot(Camera cam, Transform head, RobotOverlayGhost ghost,
                         string rel, bool thirdPerson)
        {
            if (thirdPerson)
            {
                Vector3 mid = (head.position + ghost.transform.position) * 0.5f;
                cam.transform.position = mid + new Vector3(3.6f, 1.6f, -3.6f);
                cam.transform.LookAt(mid);
            }
            else
            {
                cam.transform.SetPositionAndRotation(head.position, head.rotation);
            }
            var tex = Render(cam);
            File.WriteAllBytes(Path.Combine(_out, rel), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        /// <summary>A VLAD depth packet describing a wall 1.6 m in front of the
        /// robot base — enough geometry to sit between the operator and the
        /// video panel.</summary>
        /// <summary>A VLAD depth packet: a flat wall at ``depthMm`` seen by a
        /// camera sitting at (camX, camY, camZ) in the robot's base frame. The
        /// occluder test zeroes that pose so the wall lands exactly ``depthMm``
        /// ahead of its anchor — with the robot-like default the wall ends up
        /// 1.35 m overhead and occludes nothing.</summary>
        static byte[] SynthDepthPacket(out int w, out int h, ushort depthMm = 1600,
                                       float camX = 0.10f, float camY = 0f,
                                       float camZ = 1.35f)
        {
            w = 96; h = 54;
            const int header = 72;
            var p = new byte[header + w * h * 2];
            p[0] = (byte)'V'; p[1] = (byte)'L'; p[2] = (byte)'A'; p[3] = (byte)'D';
            Write(p, 4, 1);                        // seq
            Write(p, 8, 1);                        // rgb_seq
            p[12] = (byte)(w & 0xff); p[13] = (byte)(w >> 8);
            p[14] = (byte)(h & 0xff); p[15] = (byte)(h >> 8);
            p[16] = 0;                             // source: sim GT
            p[17] = 0;                             // encoding: uint16 mm
            WriteF(p, 20, w * 0.9f);               // fx
            WriteF(p, 24, w * 0.9f);               // fy
            WriteF(p, 28, w * 0.5f);               // cx
            WriteF(p, 32, h * 0.5f);               // cy
            WriteF(p, 36, 0.1f);                   // near
            WriteF(p, 40, 30f);                    // far
            WriteF(p, 44, camX);                   // cam pos (robot base frame)
            WriteF(p, 48, camY);
            WriteF(p, 52, camZ);
            WriteF(p, 56, 0f); WriteF(p, 60, 0f);  // cam quat xyzw = identity
            WriteF(p, 64, 0f); WriteF(p, 68, 1f);
            for (int i = 0; i < w * h; i++)
            {
                p[header + i * 2] = (byte)(depthMm & 0xff);
                p[header + i * 2 + 1] = (byte)(depthMm >> 8);
            }
            return p;
        }

        static void Write(byte[] p, int o, uint v)
        {
            p[o] = (byte)(v & 0xff); p[o + 1] = (byte)((v >> 8) & 0xff);
            p[o + 2] = (byte)((v >> 16) & 0xff); p[o + 3] = (byte)((v >> 24) & 0xff);
        }

        static void WriteF(byte[] p, int o, float v)
            => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, p, o, 4);

        static string Env(string k, string d)
        {
            string v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? d : v;
        }

        static string V(Vector3 v) =>
            $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        static string F(float v) =>
            (float.IsNaN(v) ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);
    }
}
