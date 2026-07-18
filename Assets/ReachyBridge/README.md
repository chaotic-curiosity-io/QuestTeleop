# ReachyBrain XR bridge — Meta Quest port

This folder makes a **Quest 3 / 3S** an embodied client for the ReachyBrain
XR bridge, exactly like the Viture Luma Ultra app in `VitureUnity/` — the Reachy
Mini follows the user, follows their hand, or looks where they point into a
scanned room map, and the passthrough camera can walk-capture the room into the
shared home map.

It speaks the **same wire protocol** as the Viture sender, so the Mac server
(`integrations/viture/server.py`) and the on-robot app (`apps/reachy_xr_bridge`)
run **unchanged** for the follow / hand / raycast / gaze-calibration path. The
only remote-vs-localhost difference (the camera pull) is handled by the server's
`--middle-host auto`.

## Why Quest is a better sensor than Viture here

| | Viture | Quest |
|---|---|---|
| Head pose | Carina VIO (native lib) | OVR inside-out tracking |
| Hands | MediaPipe @ **assumed 0.6 m depth** | native `OVRHand` — **metric** |
| Camera pose | not available (needed DA3 monocular reloc) | `PassthroughCameraAccess.GetCameraPose()` per frame |
| Camera intrinsics | fisheye + one-time calibration | rectilinear, `Intrinsics` from the API |

## Requirements

- Unity 6000.0.38f1+ (this project is on 6000.3.19f1), Quest 3 / 3S, Horizon OS v74+.
- MRUK v81+ (this project: v85) for `PassthroughCameraAccess`; Meta Core SDK for
  `OVRCameraRig` / `OVRHand` / `OVRInput`.
- `horizonos.permission.HEADSET_CAMERA` (already in `Assets/Plugins/Android/AndroidManifest.xml`)
  and passthrough enabled — both true in this sample project.
- Hand tracking enabled (this project: `handTrackingSupport: 1`).

## Setup — the **ReachyBridge** editor menu (easiest)

A top-level **ReachyBridge** menu (from `Editor/ReachyBridgeMenu.cs`) drives the
whole setup → build → deploy → test loop:

1. Open a passthrough scene that already has an `OVRCameraRig` + passthrough —
   the **CameraViewer** or **CameraToWorld** sample scene is ideal.
2. **ReachyBridge ▸ Settings…** → enter your Mac's LAN IP (the machine running
   the server) and toggle the camera publisher.
3. **ReachyBridge ▸ Set Up Active Scene** — adds and wires a `ReachyBridge`
   GameObject (sender + pointer + controls + HUD + camera publisher) onto the
   scene, using the host from Settings. (Run **Add Passthrough Camera** first if
   the scene has none.)
4. **ReachyBridge ▸ Build and Run on Quest** — builds the APK and installs +
   launches it on the connected headset (or **Build APK** / **Install Last Build
   (adb)** to split the steps). Quest and Mac must be on the **same Wi-Fi**.

Menu items: *Settings… · Set Up Active Scene · Add Passthrough Camera · Build APK
· Build and Run on Quest · Install Last Build (adb) · Open README · Copy Mac
Server Command*.

### Manual alternative (one component)

Create an empty GameObject, add **`ReachyBridgeBootstrap`**, set **`macHost`** to
your Mac's LAN IP. It adds and cross-wires everything at runtime; each component
also auto-finds its dependencies. The Mac IP can be changed on-device without a
rebuild via `ReachyBridgeSender.SetHost("…")` (persists in PlayerPrefs).

## Controls

Everything the Viture keyboard did, on the Touch controllers — and every action
is also a poke button on the world-space HUD panel (for hands-only use):

| Right controller | | Left controller | |
|---|---|---|---|
| **A** | gaze capture (Viture `C`) | **X** | map-calibrate (`M`) |
| **B** tap | cycle mode | **Y** | robot-locate (`R`) |
| **B** hold | clear captures (`X`) | **L-stick click** | walk-capture toggle (`B`) |
| **R-stick click** | idle / stop | **Menu** | toggle HUD panel |

Mode cycle order: `idle → follow_user → follow_hand → raycast → calibrate`.

**Pointing** (follow_hand / raycast) auto-selects the source: the controller's
forward ray when controllers are held, the `OVRHand` pointer + index pinch when
hand-tracking.

## Gaze calibration (unchanged from Viture)

The unity↔robot registration is head-gaze based and lives entirely on the Mac.
Enter **calibrate** (cycle to it, or the HUD), stand ~2 m from the robot facing
it (the robot face-locks), press **A**; move ≥1 m sideways, look at the robot,
press **A** again. Two captures → the server solves the registration. Then cycle
to `follow_user` / `follow_hand` / `raycast`.

## Mac side

```bash
# On the Mac (same Wi-Fi as the Quest):
cd ReachyBrain
./scripts/quest.sh                 # binds 0.0.0.0, resolves the camera host to
                                   # the Quest's IP automatically, prints the IP
                                   # to enter as macHost
# equivalent to:
python -m integrations.viture.server --map latest \
    --reloc-source middle --middle-host auto --cam-host auto
```

`--middle-host auto` / `--cam-host auto` make the server pull the walk-capture /
reloc camera stream from the **XR client's own IP** (learned from the :9878 TCP
connection) instead of `127.0.0.1`, which is what makes the remote Quest work
with the existing pull-based camera path.

## Files

| Script | Role |
|---|---|
| `ReachyBridgeSender.cs` | NDJSON :9878 client — head pose + hands + modes; parses `xr_status`/`xr_transforms` |
| `QuestPointerProvider.cs` | hands/controllers → `{palm, ray_o, ray_d, pinch}` (auto-detect) |
| `ReachyBridgeControls.cs` | Touch-button bindings |
| `ReachyBridgeHud.cs` | world-space status panel + poke buttons (replaces Viture's OnGUI) |
| `PassthroughVitcPublisher.cs` | passthrough camera → VITC MJPEG on :9901 for walk-capture |
| `ReachyBridgeBootstrap.cs` | adds & wires all of the above |
| `Editor/ReachyBridgeMenu.cs` | **ReachyBridge** menu — scene setup, build, deploy, settings window |

## Not yet ported / future

- **Scene mirror** (`ReachySceneAnchors` / `RoomMapRenderer`): less useful in
  passthrough (you see the real robot), so left out of this pass. The sender
  still exposes `TryGetMapFromUnity/Robot/RobotHeadMap` if you want to add it.
- **Pose-aware map calibration**: the Quest gives per-frame camera pose +
  intrinsics, which could replace the fragile DA3 monocular reloc entirely — a
  clear next step, but the gaze path already anchors the robot without it.
