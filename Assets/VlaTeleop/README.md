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
   and `VlaTeleopGizmos` (torso + arm-target debug overlay).
3. **Standalone build → the endpoints MUST be the Mac's LAN IP**, not
   `127.0.0.1` (on the deployed APK, localhost is the *headset*). Edit the
   sender's **endpoints** to your Mac, e.g. `192.168.1.152:9905` (teleop server)
   and `192.168.1.152:9906` (DevVLA). Quest + Mac must be on the same Wi-Fi.
4. Build + run on the Quest (the **ReachyBridge ▸ Build and Run on Quest** menu
   builds this scene fine too).

## Server / robot side

Easiest (same PC, editor Play mode over Link): **Tools ▸ Robot Teleop ▸ Start
Teleop Server (Quest → H1)** — spawns the server in its own console window
(that window is the live heartbeat log; Stop / Open Server Logs Folder / Set
Handtracking Folder… live in the same menu). Equivalent shell command:

```bash
cd openvla-unity-sim2real/handtracking
uv run --no-project --with numpy --with websockets \
    python teleop_server.py --robot h1 --source quest-xr   # NO --swap-hands on Quest
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
| `RobotOverlayGhost.cs` | kinematic FK poser for the semi-transparent robot ghost (33 actuated + 12 mimic joints by URDF name) |
| `RobotOverlayDriver.cs` | UDP :9907 `joint_targets` listener + anchors the ghost to the player (Superimposed / InFront) |
| `Editor/RobotOverlayBuilder.cs` | **Tools ▸ Robot Teleop ▸ Robot Overlay** — builds the H1 ghost from `Robots/h1_description/h1_with_hand.urdf` (no URDF-Importer dependency) |
| `Robots/h1_description/` | staged H1 URDF + visual `.dae` meshes (BSD-3, see `Robots/ATTRIBUTION.md`) |

## Robot ghost overlay (see the robot move on your own body)

**Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost** builds a physics-free,
semi-transparent H1 from the staged URDF (`Robots/h1_description/`) and wires a
`RobotOverlayDriver` to it. At runtime the teleop server echoes every computed
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
