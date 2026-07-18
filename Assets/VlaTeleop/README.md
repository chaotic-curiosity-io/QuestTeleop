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

## Pipeline

```
Quest (this app)                       Mac / GPU box
────────────────                       ─────────────
OVRCameraRig.centerEyeAnchor  ─┐
OVRSkeleton hands ─► 21 MediaPipe      handtracking/teleop_server.py
  landmarks (metric 3D)        ├─UDP─► --source viture-xr  (:9905)
wrist world pos (metric)       │        └► fingers + arm IK + head
                               │           └► WS :8766 ─► DevVLA GrootBridge
                               └─UDP─► DevVLA TeleopHeadCamera (:9906)
```

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

## Mac / robot side

The receivers must bind all interfaces to hear a standalone headset over the LAN
(they default to loopback, which only worked because the Viture app ran on the
same Mac):

```bash
# On the Mac (numpy + websockets only — no CV; the hands come from the Quest):
cd openvla-unity-sim2real
handtracking/run_xr_teleop.sh --robot h1 --xr-host 0.0.0.0   # NO --swap-hands on Quest
```

For the DevVLA head-camera listener (:9906), leave `TeleopHeadCamera.bindAllInterfaces`
ON (the default) so it too accepts the headset's packets. macOS may prompt to
allow incoming connections the first time — allow it, or the packets are dropped.

Then in **DevVLA**: `VLA/Setup Hand Humanoid Scene/Unitree H1` +
`VLA/Teleop Mode/Arms + Hands + Head (Viture)`, press Play. `--robot {h1,gr1,
g1_dex3}` selects the embodiment.

## Files

| Script | Role |
|---|---|
| `QuestHandLandmarks.cs` | `OVRSkeleton` bones → MediaPipe 21-landmark order (metric world) |
| `VlaTeleopSender.cs` | head + hands → `xr_pose` UDP (:9905 server, :9906 DevVLA) |
| `VlaTeleopGizmos.cs` | virtual torso + shoulder→wrist arm-target overlay |
| `Editor/VlaTeleopSceneSetup.cs` | **Tools ▸ Robot Teleop** menu |

## Calibration note

`retarget.py`'s open/closed-degree thresholds were tuned against MediaPipe's
distorted (normalized-image) angles; Quest's true 3D angles are geometrically
correct but may sit at slightly different degree values, so a finger that never
fully opens/closes is a **threshold retune**, not a bug — adjust the retargeter
`RANGES` / `finger_curl` `open_deg`/`closed_deg` on the Python side. Arm reach
scaling is `--human-reach` (your shoulder→wrist, m); per-robot arm reach is baked
into the server.

## Not compiled here

Per the openvla-unity-sim2real convention, this C# is written by careful API
review against the Meta SDK types already used in `Assets/ReachyBridge/`
(`OVRCameraRig`, `OVRHand`, `OVRSkeleton`) and has **not** been compiled in an
editor. Verify the `OVRSkeleton.BoneId` names resolve on this project's Meta
Core SDK version on first import (the thumb/pinky metacarpal ids are the ones
most likely to differ across SDK versions).
