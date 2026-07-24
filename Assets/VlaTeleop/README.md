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
| `VlaTeleopSender.cs` | head + hands + body → `xr_pose` UDP (:9905 server, :9906 DevVLA); 5 s heartbeat log |
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
● REC 142f                                                <- only while recording
```

Sender line: `✓` = tracked high-confidence, `~` = low confidence (the server
HOLDs those frames), `–` = untracked; `legs` needs FullBody body tracking.
Ghost line: applied joints, echo packets/s, cumulative `drop:` (seq gaps),
the server-honored mode in `[brackets]`, and `STALE n.ns` when no echo arrived
for >0.5 s. The red **● REC nf** shows the server-side raw recorder state
(frame count rides in every echo packet).

These OnGUI HUDs only render on the PC mirror view. For an **in-headset**
panel, run **Tools ▸ Robot Teleop ▸ Add Floating HUD Panel** — a lazy-follow
world-space canvas 0.9 m ahead showing both ends of the loop (send Hz, hand
conf, body/legs, mode, echo Hz/drops/staleness, REC) plus the live
index/middle pinch strengths for gesture debugging. Toggle via its `visible`
checkbox.

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

* **Superimposed** (default): the ghost stands where you stand — feet on the
  tracking floor, yaw from your measured shoulder line (head fallback). You
  view it from inside; glance down at your arms. Backface culling hides most
  of the torso shell from within.
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

## Recording for fine-tuning (collect once, retarget to many)

The server records **raw human episodes** (`teleop_raw_v1`: the verbatim
`xr_pose` packets + the mapped targets + the mapper state), not robot-specific
joint logs — one take retargets offline to ANY robot. Flow:

1. Start the server via the menu (it passes `--record-raw episodes`). The
   recorder is ARMED, not recording.
2. In VR: **double pinch** (thumb+index on both hands) to start — the red
   **● REC nf** appears on the HUD. Double pinch again to stop.
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
smoothing. A finger that never fully opens/closes is a threshold retune in
that file — watch the server heartbeat's curl values with an open hand vs a
fist and adjust. Body tracking makes the chest offsets moot (measured
shoulders win whenever valid). Verify body tracking itself with the Movement
SDK sample scene `Assets/Samples/.../MovementBody.unity` — if the skeleton
looks right there, the anchors feeding this sender are good.

## Not compiled here

Per the openvla-unity-sim2real convention, this C# is written by careful API
review against the Meta SDK types already used in `Assets/ReachyBridge/`
(`OVRCameraRig`, `OVRHand`, `OVRSkeleton`) and has **not** been compiled in an
editor. Verify the `OVRSkeleton.BoneId` names resolve on this project's Meta
Core SDK version on first import (the thumb/pinky metacarpal ids are the ones
most likely to differ across SDK versions).
