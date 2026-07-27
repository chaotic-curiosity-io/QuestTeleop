# VLA XR teleop — Meta Quest port

This folder makes a **Quest 3 / 3S** a teleoperation client for the
**openvla-unity-sim2real** robot pipeline — the second of VitureUnity's two
bridges. (The first, follow/hand/gaze to the ReachyBrain Mac server, is already
ported in `Assets/ReachyBridge/`.)

Where ReachyBridge drives the *ReachyBrain* app's follow modes, **VlaTeleop
drives real robot joints**: your head pose + finger poses + wrist positions
become the robot's head joints, finger curls, and arm IK targets. It is the
Quest equivalent of `VitureUnity/Assets/Scripts/VlaTeleopSender.cs`
("Pipeline 3 — XR teleop") and speaks the **exact same `xr_pose` UDP packet**,
so the Python side runs **unchanged**.

## Pipeline (quest-xr — the Quest-native path)

```
Quest (this app)                       Server machine
────────────────                       ──────────────
OVRCameraRig.centerEyeAnchor  ─┐
OVRSkeleton hands ─► 21 MediaPipe      handtracking/teleop_server.py
  landmarks (metric 3D) + conf ├─UDP─► --source quest-xr  (:9905)
wrist world pos + quat (metric)│        ├► fingers: interior angles, thresholds
OVRBody chest + shoulders      │        │   from profiles/quest_h1.json
  (QuestBodyAnchors)           │        ├► arms: ArmIK solve → quest_arm.py
                               │        │   EXACT URDF chain map (NO TUNING),
                               │        │   anchored at your MEASURED shoulders
                               │        │   (virtual chest = fallback)
                               │        ├► WS :8766 ─► DevVLA GrootBridge
                               │        └► joint-target echo (target_echo.py)
                               │             └─UDP :9907─► RobotOverlayDriver
                               │                           (H1 ghost, this app)
                               └─UDP─► DevVLA TeleopHeadCamera (:9906)
```

`--source quest-xr` is a SEPARATE worker from the Viture path (`viture-xr`):
same packet transport and finger-curl math, but the arm mapping bypasses the
`arm_retarget.TUNING` affine entirely (that table is a crutch for monocular
webcam angles) and body tracking replaces the head-offset chest heuristic
whenever it's valid. Calibration lives in per-source profile JSONs
(`handtracking/profiles/quest_h1.json`), and the server logs repeatedly:
a console heartbeat every 2 s + per-frame JSONL stage taps in
`handtracking/logs/` (raw packet → torso-frame wrist → IK → joint targets).

The finger retargeters (`handtracking/retarget.py`) compute each curl as an
**interior joint angle** from three landmark positions — frame-invariant — so
all they need is the 21 points in MediaPipe topology. `QuestHandLandmarks.cs`
remaps the native `OVRSkeleton` bones into that order. **No Python change.**

## Why Quest beats Viture on this path

| | Viture (VlaTeleopSender) | Quest (this) |
|---|---|---|
| Head pose | Carina VIO native lib | OVR inside-out (`centerEyeAnchor`) |
| Hands | MediaPipe over a :8778 bridge | native `OVRSkeleton` |
| Finger angles | monocular, anisotropic image coords | **true metric 3D joints** |
| Wrist EE target | landmark 0 @ **assumed 0.6 m depth** | **true metric world position** |
| Handedness | egocentric mirror → needs `--swap-hands` | correct (`HandLeft`/`HandRight`) → **no swap** |

## Setup

1. Open a scene with an `OVRCameraRig` + hand tracking — any
   PassthroughCameraApiSamples scene, or one from **ReachyBridge ▸ Set Up Active
   Scene**. (Hand tracking is already enabled in this project.)
2. **Tools ▸ Robot Teleop ▸ Add VLA Teleop Sender** — adds a `RobotTeleop`
   object with `VlaTeleopSender` (auto-binds the rig + both `OVRHand`s at Play)
   and `VlaTeleopGizmos` (torso + arm-target debug overlay). It also creates
   the `BodyTrackingAnchors` object and runs **Enable Body Tracking** (below),
   so `body:✓` should light up without extra steps.
   - **Tools ▸ Robot Teleop ▸ Enable Body Tracking (Project + Scene)** flips
     everything body tracking needs in one click: the OVRProjectConfig
     capability, the `BODY_TRACKING` entry in the custom AndroidManifest, the
     OVRManager startup permission prompt, and warns if the runtime joint set
     isn't FullBody (legs need it). Over Link ALSO enable "Developer runtime
     features" + body tracking in the Meta Quest Link PC app — without it the
     HUD stays `body:–` even though everything above is on.
3. **Standalone build → the endpoints MUST be the Mac's LAN IP**, not
   `127.0.0.1` (on the deployed APK, localhost is the *headset*). Edit the
   sender's **endpoints** to your Mac, e.g. `192.168.1.152:9905` (teleop server)
   and `192.168.1.152:9906` (DevVLA). Quest + Mac must be on the same Wi-Fi.
4. Build + run on the Quest (the **ReachyBridge ▸ Build and Run on Quest** menu
   builds this scene fine too).

## Server / robot side

Easiest (same PC, editor Play mode over Link): **Tools ▸ Robot Teleop ▸ Start
Teleop Server (Quest → H1)** (or **… (Quest → GR-1)**) — spawns the server in
its own console window (that window is the live heartbeat log; Stop / Open
Server Logs Folder / Set Handtracking Folder… live in the same menu). The menu
passes `--record-raw episodes`, which ARMS the raw-episode recorder — a
**double pinch** (thumb+index, both hands) toggles recording; see "Recording
for fine-tuning" below. Equivalent shell command:

```bash
cd openvla-unity-sim2real/handtracking
uv run --no-project --with numpy --with websockets \
    python teleop_server.py --robot h1 --source quest-xr \
    --record-raw episodes                      # NO --swap-hands on Quest
```

For a STANDALONE headset over the LAN add `--xr-host 0.0.0.0` (receivers
default to loopback, which only hears a same-machine sender).

For the DevVLA head-camera listener (:9906), leave `TeleopHeadCamera.bindAllInterfaces`
ON (the default) so it too accepts the headset's packets. macOS may prompt to
allow incoming connections the first time — allow it, or the packets are dropped.

Then in **DevVLA**: `VLA/Setup Hand Humanoid Scene/Unitree H1` +
`VLA/Teleop Mode/Arms + Hands + Head (Viture)`, press Play. `--robot {h1,gr1,
g1_dex3}` selects the embodiment.

## Files

| Script | Role |
|---|---|
| `QuestHandLandmarks.cs` | `OVRSkeleton` bones → MediaPipe 21-landmark order (metric world) + wrist rotation |
| `QuestBodyAnchors.cs` | `OVRBody` → world chest + shoulder anchors (Movement-SDK body tracking) |
| `VlaTeleopSender.cs` | head + hands + body → `xr_pose` UDP (:9905 server, :9906 DevVLA); carries the `cmd`/`scrub` transport blocks; 5 s heartbeat log |
| `TeleopGestureCommands.cs` | hand poses → transport commands (record/pause/rewind/playback); ISDK `ShapeRecognizerActiveState` when wired, built-in landmark tests otherwise; pinch-drag timeline scrub |
| `Editor/TeleopGestureSetup.cs` | **Tools ▸ Robot Teleop ▸ Gestures** — add the transport, build/remove the ISDK pose recognizers |
| `VlaTeleopGizmos.cs` | virtual torso + measured (magenta) shoulders + shoulder→wrist arm-target overlay |
| `Editor/VlaTeleopSceneSetup.cs` | **Tools ▸ Robot Teleop** scene setup menu |
| `Editor/TeleopServerMenu.cs` | **Tools ▸ Robot Teleop** start/stop the Python server, open logs |
| `RobotOverlayGhost.cs` | kinematic FK poser for the semi-transparent robot ghost (joints matched by URDF name — robot-agnostic) |
| `RobotOverlayDriver.cs` | UDP :9907 `joint_targets` listener + anchors the ghost to the player (Superimposed / InFront); HUD with rate/drops/staleness + red **● REC** indicator |
| `Editor/RobotOverlayBuilder.cs` | **Tools ▸ Robot Teleop ▸ Robot Overlay** — builds the H1 or GR-1 ghost from the staged URDFs (no URDF-Importer dependency; `.dae` via the Collada fix, `.STL` via pre-baked importer prefabs) |
| `Robots/h1_description/` | staged H1 URDF + visual `.dae` meshes (BSD-3, see `Robots/ATTRIBUTION.md`) |
| `Robots/gr1t2_description/` | staged GR-1 T2 URDF + baked STL mesh prefabs (see `Robots/ATTRIBUTION.md`) |
| `Robots/g1_description/` | staged Unitree G1 (29 DoF + Dex3) URDF + baked STL mesh prefabs |
| `VlaTeleopHudPanel.cs` | in-VR floating metrics panel (world-space canvas, lazy follow) — the OnGUI HUDs are PC-mirror-only |

## HUD (what the two lines mean)

```
VLA teleop  sent:157  hands L:✓ R:~  body:✓ legs:–        <- sender
Robot ghost :9907  robot:h1 joints:33 30/s drop:2  mode:Superimposed
● RECORDING  142f  4.7/9.2s  recording                    <- transport state
```

Sender line: `✓` = tracked high-confidence, `~` = low confidence (the server
HOLDs those frames), `–` = untracked; `legs` needs FullBody body tracking.
Ghost line: applied joints, echo packets/s, cumulative `drop:` (seq gaps),
the server-honored mode in `[brackets]`, and `STALE n.ns` when no echo arrived
for >0.5 s. The transport line is the session state from `session.py` —
`● RECORDING` / `❚❚ PAUSED` / `◀◀ SCRUB` / `▶ PLAYBACK` with the frame count,
playhead and take length (all of it rides in every echo packet).

These OnGUI HUDs only render on the PC mirror view. For an **in-headset**
panel, run **Tools ▸ Robot Teleop ▸ Add Floating HUD Panel** — a lazy-follow
world-space canvas 0.9 m ahead showing both ends of the loop (send Hz, hand
conf, body/legs, mode, echo Hz/drops/staleness), the transport state with a
timeline bar, the dwell bar of whatever gesture you are currently holding, and
the live index/middle pinch strengths. Toggle via its `visible` checkbox.

## Teleop modes (what the robot follows)

The sender stamps every packet with a scope the server masks by (`mode` field;
masked groups HOLD their last targets):

| Mode | drives |
|---|---|
| `FullBody` (default) | fingers + arms/wrists + torso + head + legs |
| `UpperBody` | fingers + arms/wrists + torso + head |
| `HandsOnly` | finger curls only |

Cycle with a **double MIDDLE-finger pinch** on either hand (shut → open →
shut within 1.5 s), the component context menu ▸ Cycle Mode, or the Inspector.
The index-finger double pinch is separate — that one toggles recording
server-side. Raw recordings store the mode per frame, so offline retargets
mask identically. The ghost HUD shows the mode the server actually honored.

## Robot ghost overlay (see the robot move on your own body)

**Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost** builds a physics-free,
semi-transparent H1 from the staged URDF (`Robots/h1_description/`) and wires a
`RobotOverlayDriver` to it. **Build GR-1 Ghost** (Fourier GR-1 T2, 44 actuated
+ 10 mimic) and **Build G1 Ghost** (Unitree G1 + Dex3 three-finger hands, 43
actuated) do the same from their staged URDFs — build ONE ghost at a time
(every driver binds :9907; the second logs a bind error) and start the
matching server (**Start Teleop Server (Quest → H1 / GR-1 / G1)**).

Both GR-1 and G1 add 7-DoF arms whose **wrist joints follow your measured
hand orientation** (GR-1: wrist yaw/roll/pitch; G1: roll/pitch/yaw). GR-1
additionally has 3 neck joints following your head (G1 and H1 have no neck).
Torso yaw drives `torso_joint` (H1) or `waist_yaw_joint` (GR-1/G1). Dex3's
left-hand finger joints are sign-mirrored from the right's — handled by the
layout (`left_hand_sign`), not calibration. At runtime the teleop server echoes every computed
joint-target vector back to this machine (`handtracking/target_echo.py`, UDP
**:9907**, destination = wherever the xr_pose packets came from — loopback in
editor Play, the headset's LAN IP standalone), and the driver FK-poses the
ghost with them. You see the ROBOT'S pose overlaid on your own movements —
the retargeting offsets are visible by design; that's what the overlay is for.

* **Superimposed** (default): the ghost stands where you stood — feet on the
  tracking floor, yaw from your measured shoulder line (head fallback). You
  view it from inside; glance down at your arms. Backface culling hides most
  of the torso shell from within. The root is solved **once** (and on
  Recenter), because the robot it mirrors has an immovable base and receives
  your body yaw as a waist JOINT target in the echo — re-solving it every
  frame applied your yaw a second time, so the ghost turned at 2× your rate
  and the point cloud parented to this root at 3×. `followPlayer` restores the
  old chase behaviour. Measured headless (2026-07-25, G1 + H1): ghost root yaw
  gain 1.00 → 0.00, drift of a fixed point of the robot's world 2.369 m →
  0.000 m; harness `Editor/VLATeleopAnchorHeadlessTest.cs`, artifacts in the
  main repo's `media/2026-07-25_teleop-yaw-anchor-fix/`.
* **InFront**: parked 1.5 m ahead, facing you — the calibration-check view
  (not a mirror: your left arm drives its left arm). Component context menu ▸
  **Recenter** re-anchors it.

Config: profile keys `echo_targets` (default on) / `echo_port` (9907) in
`profiles/quest_h1.json`. Standalone headset: nothing extra — the echo goes to
the packet's source address automatically; just make sure UDP :9907 inbound is
allowed on the headset build (it is by default). Legs/torso on the ghost only
move with FullBody tracking + `drive_legs`/`drive_torso`; otherwise they hold
the rest pose (correct, not a bug). Verify after building: the ghost stands
upright at the origin facing +Z; single-joint sanity checks live in the
`RobotOverlayBuilder.cs` header comment.

## Full body (torso + legs)

With FullBody body tracking (generative legs), the packet also carries
hip/knee/ankle/toe anchors per side. The server then:
* yaws H1's `torso_joint` to follow your **shoulder line** (not your head —
  looking around no longer moves the robot or the arm anchors), and
* mirrors your leg ANGLES onto hip/knee/ankle (`profiles/quest_h1.json`
  `drive_legs`) — a **pinned-base puppet**: position control cannot balance a
  free biped, so keep the robot root Immovable.
In DevVLA pick **Teleop Mode ▸ Full Body (Quest)** to unmask torso+legs.
H1 has **no neck joints** — head motion drives only DevVLA's EgoCam view
(GR-1 is the robot with a real neck).

`auto_reach` (profile, default on) grows your arm-reach estimate to the
maximum shoulder→wrist distance it observes, so a fully extended human arm
maps to a fully extended robot arm without measuring yourself.

## Transport controls: record, pause, rewind, play back — with your hands

Both your hands are busy being the robot, so the record button has to *be* a
hand. `TeleopGestureCommands.cs` recognizes deliberate poses and stamps a
transport command into the outgoing `xr_pose` packet; `handtracking/session.py`
runs the state machine and reports back over the joint-target echo, which is
what the HUD's ● REC / timeline reads.

| gesture | does |
|---|---|
| 👍 **thumbs up** (right) | start recording — and resume a paused take |
| ✋ **open palm** (left, facing away) | pause: the robot **freezes** where it is |
| 👎 **thumbs down** (right) | stop + save the episode |
| ✌️ **peace sign** (either) | step out of the robot and play the take back (again = take control back) |
| 🤏 **pinch + drag** (left index, hold 0.5 s) | scrub the timeline; release to **splice** |
| 👎 **left** thumbs-down (hold 1.5 s) | recenter the robot/origin on you (see below) |

### Keyboard shortcuts (editor Play mode / over Link)

Poses are the real control surface, but a keyboard is unambiguous — use it
while calibrating, and as the way out when a pose will not register:

| key | does |
|---|---|
| **R** | **recalibrate / recenter** |
| B | begin recording |
| P | pause |
| S | stop + save |
| K | play the take back |

Rebindable on the `TeleopGestureCommands` component (`keyboardShortcuts`), by
key NAME. There is no keyboard on a standalone headset build, so this is an
editor/Link facility only — the poses still have to work for untethered use.

This project runs the **new Input System only** (`activeInputHandler: 1`),
where the legacy `UnityEngine.Input` throws on first use. The lookup is written
against both backends and validated by `IsKeyNameValid`, so a key name that
does not resolve is caught at setup rather than by silence.

Each pose must be held ~0.5 s before it fires, and released before it can fire
again — the HUD panel shows a dwell bar while you hold one, so a gesture that
isn't registering looks different from a server that never answered.

**Pause holds, it does not follow.** A paused robot stays exactly where it was.
That's what makes rewind meaningful — the robot is a scrubbable playhead, not a
mirror — and it means you can drop your arms without putting a lurch in the
demo.

**Resume ramps, and the ramp is not recorded.** Coming back from a pause your
arms are somewhere else entirely. The robot interpolates from the held pose to
your live pose over `resume_blend_s` (profile, default 1 s) and only *then*
starts writing frames again, so the take is continuous even though you weren't.
Paused time is excluded from the take's clock too (`t_rel` in each row), so a
five-minute break doesn't become a five-minute freeze in the training data.

**Rewind splices.** Pinch, hold, and drag your hand sideways: the robot rewinds
*with* your hand, showing the frame under the cursor. Release and the frames
after the cursor are dropped and the recording clock rewinds with them — so
when you resume, you re-record over the bad span instead of appending after it.
Every edit is logged in the episode's `meta.json` (`edits: [{op, t_rel,
frame, dropped}]`), so a spliced demo stays auditable.

**Playback drives the real robot.** Stepping out replays the take through the
same `TeleopState` the mapper writes, so it reaches Unity over the WebSocket
chunk stream that is already running: the DevVLA robot moves and the ghost
moves, with no playback code on either Unity side.

**The ghost walks out and performs.** During playback it leaves your body,
walks to a stand `playbackDistance` (2.2 m) ahead, turns to face you, and acts
out the recorded joint motion — and **its point cloud comes with it**
(`RobotOverlayDriver.pointCloud`). That hand-off matters: the cloud normally
rides its own parked anchor, so without it the cloud would perform the take
where it happens to be while the ghost walked out empty-handed — the robot and
the world it perceived performing in two different places. When playback ends
both come home and the ghost re-plants on you.

### Two lessons about pose design

Both cost a live session, and both generalise to any gesture you add:

1. **An open hand is a resting pose, so finger shape alone cannot be a
   command.** PAUSE keys on the palm being turned at *your own face*, and that
   test is measured against your head's **position**, not its forward vector —
   you glance around constantly while teleoperating, and a gaze-relative test
   turns every relaxed hand into a pause.
2. **Closed-hand poses report LOW tracking confidence**, because the fingers
   occlude each other — that is the tracker working correctly. Gating gesture
   recognition on `IsDataHighConfidence` therefore makes fists and thumbs-down
   silently unfirable while looking exactly like a broken recognizer. The dwell
   timer is the noise filter instead (`requireHighConfidence`, default **off**).
   The teleop *stream* still holds on low-confidence frames — a jittery robot
   arm and a jittery button are not the same risk.

   **That gate lives in TWO places** and both must be lifted:
   `TeleopGestureCommands.Tracked()` *and* `QuestHandLandmarks.TryFill()`, which
   has its own `!IsDataHighConfidence` early-out. Relaxing only the first one
   changes nothing at all — the landmarks never get filled, so no closed-hand
   pose can ever evaluate. `TryFill` still defaults to requiring high confidence
   (correct for the teleop stream); gesture callers pass `false`.
3. **Resolve the head transform lazily.** `Start()` order between this component
   and `VlaTeleopSender` is not guaranteed, so binding it once can capture a
   still-null field and leave every palm test without a reference for the whole
   session. Palm tests now **fail closed** when there is no head, rather than
   falling back to a fixed world axis — a fallback like that fires wherever you
   happen to stand, which reads as "the orientation check does nothing".

The 5 s heartbeat prints a verdict per binding per hand — `.` untracked,
`-` no shape, `s` shape but orientation rejected, `S` firing — so "nothing
happens" tells you *which* half to fix.

### Recalibrating at runtime

If the robot starts out at an odd offset, that is the **Superimposed anchor
captured once, too early** — before body tracking settled, or before you stood
where you meant to. Hold a **left-hand thumbs-DOWN** for **1.5 s**.

Same thumb-out shape as REC and SAVE, separated by hand and thumb direction —
and both of those live on the *right* hand, so nothing collides.

Two earlier poses for this were tried and abandoned in live sessions: two
fists, then two open palms framing each other. Both were theoretically better
(a two-handed pose is essentially impossible to trigger by accident) and both
failed to fire reliably. **For in-VR controls, a pose you can strike every time
beats a pose that can't misfire.** It re-plants the
ghost root on your current pose, re-parks the point cloud and the video panel,
and tells the server to clear its torso-yaw zero so arm anchoring re-references
from how you are standing *now*. It works mid-take (the seam is logged to the
episode's `meta.json` rather than silently smoothed over), and the local half
runs even with the server down.

Setup: **Tools ▸ Robot Teleop ▸ Gestures ▸ Add Gesture Transport**, then
optionally **Build Gesture Poses (ISDK)** — see the next section. Nothing needs
`--record-raw` for playback of episodes already on disk; recording does.

### Pose recognition (Interaction SDK)

Shape recognition uses the Interaction SDK's pose system, the pattern in
`Assets/Samples/Meta XR Interaction SDK/…/Example Scenes/PoseExamples.unity`:

```
Hand (IHand)
  └─ FingerFeatureStateProvider          per-finger curl/flexion state machine
       └─ ShapeRecognizerActiveState     live hand vs. one or more…
            └─ ShapeRecognizer (.asset)  ThumbUp, FingersAllOpen, FingersScissors…
```

PoseExamples feeds the resulting `IActiveState` into an `ActiveStateSelector` →
`SelectorUnityEventWrapper` → UnityEvents. We read the same bool one step
earlier and add a dwell timer plus a world-orientation test. Reading it
ourselves is what lets one shape mean two things: **thumbs up and thumbs down
are the same `ShapeRecognizer`** — only the thumb's world direction differs.

### One-click setup (do this)

**Tools ▸ Robot Teleop ▸ Gestures ▸ Set Up ISDK Hand Poses in H1-Quest** opens
that scene, does everything below, and saves it. (**… (Active Scene)** does the
same to whatever scene is open.) Safe to re-run — it is idempotent.

It installs the **`OVRComprehensiveInteractionRig`** prefab under your
`OVRCameraRig` — the same rig `PoseExamples.unity` uses. The Quick Actions
wizard that normally adds it is an `internal` class we cannot call, so the tool
instantiates the prefab by GUID and does the wizard's follow-up work itself:
wiring `OVRCameraRigRef._ovrCameraRig` (the component asserts on it at Start,
so an unwired rig throws the moment you press Play) and disabling the Core
block's duplicate hand visuals, which would otherwise give you two offset sets
of hands.

Then it adds the gesture transport, resolves a `FingerFeatureStateProvider` per
hand, builds one `ShapeRecognizerActiveState` per (binding, hand) under a
`TeleopGesturePoses` object, and assigns them. **Remove Gesture Poses** tears
the recognizer half back down.

Two things worth knowing about that wiring:

* **The rig contains ~18 `Hand` components**, including *synthetic* ones that
  replay a pose rather than reporting your live finger curls. Pose detection
  hung off one of those would simply never fire. The tool therefore prefers the
  providers the rig already ships (bound to the tracked hands) and only creates
  its own if none exist.
* **A recognizer watches ONE hand**, so `Both` / `Either` bindings need two.
  Bindings have `shapeState` (left) and `shapeStateRight` — reusing one
  recognizer for both sides would quietly turn "both hands" into "whichever
  hand that recognizer happens to watch".

> **Two things are both called "hand tracking".** These scenes use the
> Building Blocks **Hand Tracking** block — category *Core* — which adds
> `OVRHand` + `OVRSkeleton`. That is exactly what the teleop stream and the
> built-in poses read, and it is why hand tracking visibly works. It does
> **not** add `Oculus.Interaction.Input.Hand`, which is a different component
> that only the SDK's pose recognizers need. The block that used to bridge
> them ("Hand Interactions") is tagged Hidden + Deprecated, so you will not
> find it in the Building Blocks window. The supported route is the Quick
> Actions wizard **GameObject ▸ Interaction SDK ▸ Add OVR Comprehensive
> Interaction Rig** — the same rig `PoseExamples.unity` uses. Build Gesture
> Poses offers to open it for you. None of this is required: the built-in
> poses work with the `OVRHand` you already have.

Orientation deliberately does *not* use `TransformRecognizerActiveState`: that
needs an `IHmd` and a tracking-to-world transformer threaded through the rig,
while `QuestHandLandmarks` already gives metric world joints that answer "is
the thumb pointing up" exactly.

Without the ISDK step everything still works — each binding carries a built-in
finger test computed from those same landmarks (interior joint angles, the same
idea as the Python finger retargeters). The SDK path is better (calibrated,
hysteretic, inspector-editable), but a scene with nothing but an `OVRCameraRig`
and hand tracking is not left without transport controls.

Rebind anything in the `TeleopGestureCommands` inspector — the server only ever
sees the command name.

## Recording for fine-tuning (collect once, retarget to many)

The server records **raw human episodes** (`teleop_raw_v1`: the verbatim
`xr_pose` packets + the mapped targets + the mapper state), not robot-specific
joint logs — one take retargets offline to ANY robot. Flow:

1. Start the server via the menu (it passes `--record-raw episodes`). The
   recorder is ARMED, not recording.
2. In VR: **thumbs up** to start — the red **● RECORDING** line appears on the
   HUD with the frame count and take length. Pause / rewind / save with the
   gestures above. (A **double pinch** — full fist twice — still toggles
   recording as the no-setup fallback.)
3. Offline, in `handtracking/`:

```bash
# retarget the raw episode to a robot (same-robot replays auto-verify bit-exact)
uv run --no-project --with numpy python retarget_episode.py \
    episodes/episode_000000 --robot h1        # or --robot gr1

# QA gate (rejects short/frozen/spiky episodes before they hit training)
uv run --no-project --with numpy python ../server/tools/episode_quality.py \
    episodes/episode_000000/retarget_h1

# GR00T-flavored LeRobot v2.1 dataset (absolute joint state/action)
uv run --no-project --with numpy --with pyarrow python \
    ../tools/convert_teleop_to_lerobot.py \
    --src episodes --robot h1 --out /path/h1_teleop_v1
```

The converter emits real URDF joint names and the v2.1 layout GR00T consumes
(`data/chunk-000/*.parquet` + `meta/`). Ego-view video is rendered by
replaying `steps.jsonl` in the robot's DevVLA scene (frames/ next to
steps.jsonl are picked up automatically on re-convert); without frames the
dataset is state/action-only and the converter says so.

## Calibration

All person/headset tunables live in `handtracking/profiles/quest_h1.json`
(NOT Python source): finger open/closed-degree thresholds, thumb spread,
`human_reach` (your shoulder→wrist, m), chest-anchor fallback offsets,
smoothing, and the transport knobs `resume_blend_s` / `playback_loop` /
`playback_speed` / `gesture_transport` (set the last to `false` to ignore
in-headset commands entirely). A finger that never fully opens/closes is a threshold retune in
that file — watch the server heartbeat's curl values with an open hand vs a
fist and adjust. Body tracking makes the chest offsets moot (measured
shoulders win whenever valid). Verify body tracking itself with the Movement
SDK sample scene `Assets/Samples/.../MovementBody.unity` — if the skeleton
looks right there, the anchors feeding this sender are good.

## Compile status

The transport scripts (`TeleopGestureCommands.cs`, `Editor/TeleopGestureSetup.cs`,
and the sender / overlay-driver / HUD changes) **compile** — verified headlessly
with the Unity CLI against a scratch project holding the three Meta packages,
with a probe asserting the transport wire names, the default bindings, the
session glyphs, the `cmd`/`scrub` packet JSON and the `session` echo block's
`JsonUtility` round-trip.

What is still unverified is *behavior in a headset*: gesture dwell times and
the built-in pose thresholds (`Straightness` cut-offs, the 0.5 orientation
dots) are first-guess numbers, and the ISDK recognizer path has never run
against a real `OVRComprehensiveInteractionRig`. Expect a tuning pass on the
first live session — the HUD's dwell bar is there to make that quick.

Older scripts in this folder predate the CLI and were written by API review
only; if `OVRSkeleton.BoneId` names ever drift across Meta Core SDK versions,
the thumb/pinky metacarpal ids are the ones most likely to break.
