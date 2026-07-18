# QuestTeleop

**Meta Quest 3 / 3S as an XR teleoperation client for robot / VLA pipelines.**

QuestTeleop turns a Quest headset into an embodied controller: your head pose,
hand poses, and pointing drive a real or simulated robot. It carries two
independent bridges, both built on Meta's Passthrough Camera API — you stream
head + native hand tracking over the network, a host-side server retargets it to
a robot, and you see the result in passthrough.

| Bridge | Drives | Talks to |
|---|---|---|
| **`Assets/VlaTeleop/`** | robot **joints** — finger curls, arm IK, head — for humanoids (Unitree H1 / G1, Fourier GR-1, …) | [openvla-unity-sim2real](https://github.com/chaotic-curiosity-io/robotics-unity) `handtracking/teleop_server.py` → DevVLA |
| **`Assets/ReachyBridge/`** | a **Reachy Mini** in follow-user / follow-hand / raycast / gaze modes | the ReachyBrain XR bridge (Mac server + on-robot app) |

Each subfolder has its own README with the full setup; this one is the map.

## Why Quest for teleop

Quest gives everything the sensor side of teleop wants, natively and metric:

| | typical monocular rig | Quest |
|---|---|---|
| Head pose | external VIO / SLAM lib | OVR inside-out (`centerEyeAnchor`) |
| Hands | MediaPipe @ assumed depth | native `OVRSkeleton` — **true metric 3D** |
| Camera pose | monocular relocalization | `PassthroughCameraAccess.GetCameraPose()` per frame |
| Camera intrinsics | one-time calibration | rectilinear `Intrinsics` from the API |
| Handedness | egocentric mirror ambiguity | correct (`HandLeft` / `HandRight`) |

## Requirements

- **Quest 3 / 3S** (older Quest hardware doesn't expose passthrough camera frames).
- **Unity 6000.3.19f1** (this project's version; `ProjectSettings/ProjectVersion.txt`).
- **MRUK v85** (`com.meta.xr.mrutilitykit` 85.0.0) for `PassthroughCameraAccess`;
  Meta Core SDK for `OVRCameraRig` / `OVRHand` / `OVRSkeleton` / `OVRInput`.
- **Horizon OS v74+**, hand tracking enabled, and the
  `horizonos.permission.HEADSET_CAMERA` permission + passthrough
  (both already set in this project — see `Assets/Plugins/Android/AndroidManifest.xml`).
- The **XR Simulator does not support the Passthrough Camera API** — test on a
  physical device or via Meta Horizon Link.

## Quick start — VLA joint teleop (`Assets/VlaTeleop/`)

1. Open a scene with an `OVRCameraRig` + hand tracking (any
   PassthroughCameraApiSamples scene, or one from **ReachyBridge ▸ Set Up Active
   Scene**).
2. **Tools ▸ Robot Teleop ▸ Add VLA Teleop Sender** — adds a `RobotTeleop`
   object (sender + debug gizmos), auto-binding the rig and both hands at Play.
3. **Set the sender's endpoints to your host machine's LAN IP** — on a standalone
   build, `127.0.0.1` is the *headset*. e.g. `192.168.1.152:9905` (teleop server)
   and `192.168.1.152:9906` (DevVLA head camera). Headset + host on the same Wi-Fi.
4. Build + run on the Quest (**ReachyBridge ▸ Build and Run on Quest** builds any
   scene in this project).
5. On the host, in [openvla-unity-sim2real](https://github.com/chaotic-curiosity-io/robotics-unity):
   ```bash
   handtracking/run_xr_teleop.sh --robot h1 --xr-host 0.0.0.0
   ```
   then in DevVLA run `VLA/Setup Hand Humanoid Scene/Unitree H1` +
   `VLA/Teleop Mode/Arms + Hands + Head` and press Play.

Full detail, the wire protocol, and calibration notes: **[`Assets/VlaTeleop/README.md`](Assets/VlaTeleop/README.md)**.

## Quick start — ReachyBrain XR bridge (`Assets/ReachyBridge/`)

Use the top-level **ReachyBridge** menu (Settings → Set Up Active Scene → Build
and Run on Quest). Follow / hand / raycast / gaze modes on the Touch controllers
or hand tracking. Full detail: **[`Assets/ReachyBridge/README.md`](Assets/ReachyBridge/README.md)**.

## Repository layout

```
Assets/
  VlaTeleop/                 VLA joint teleop — xr_pose UDP :9905/:9906
    QuestHandLandmarks.cs      OVRSkeleton -> MediaPipe 21-landmark order (metric)
    VlaTeleopSender.cs         head + hands -> xr_pose UDP
    VlaTeleopGizmos.cs         torso + arm-target debug overlay
    Editor/VlaTeleopSceneSetup.cs   Tools ▸ Robot Teleop menu
  ReachyBridge/              ReachyBrain follow/hand/raycast/gaze bridge (NDJSON :9878)
  PassthroughCameraApiSamples/   the upstream Meta sample scenes (unmodified)
  ...
```

Meta's original sample scenes (`CameraViewer`, `CameraToWorld`,
`BrightnessEstimation`, `MultiObjectDetection`, `ShaderSample`) are kept intact —
they're the reference for the passthrough-camera plumbing the teleop bridges
build on.

## Related projects

- **[openvla-unity-sim2real](https://github.com/chaotic-curiosity-io/robotics-unity)** —
  the host-side teleop server + DevVLA robot scenes the VlaTeleop bridge drives.
- **VitureUnity** — the Viture Luma Ultra sibling app; QuestTeleop's VlaTeleop is
  a port of its `VlaTeleopSender` ("Pipeline 3 — XR teleop") with native Quest
  hands in place of MediaPipe.

## Attribution & license

QuestTeleop is derived from Meta's
**[Unity-PassthroughCameraApiSamples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)**
and retains those samples and their license. See **`LICENSE.txt`**
(© Meta Platforms, Inc.) and the per-sample license under
`Assets/PassthroughCameraApiSamples/`. The `MultiObjectDetection` sample uses a
YOLO model under MIT. The `VlaTeleop/` and `ReachyBridge/` bridges are additions
by this project's authors.
