// ReachyBrain XR bridge — Quest port. One-component bootstrap.
//
// Drop this on an empty GameObject in a passthrough-enabled scene (the Meta
// CameraViewer / CameraToWorld sample scenes already have an OVRCameraRig with
// passthrough + the HEADSET_CAMERA permission wired). On Awake it adds and
// cross-wires the whole bridge so nothing else needs manual scene setup:
//
//   QuestPointerProvider     hands/controllers -> palm/ray/pinch
//   ReachyBridgeSender       NDJSON :9878 client (head pose + hands + modes)
//   ReachyBridgeControls     Touch-button bindings
//   ReachyBridgeHud          world-space status panel + poke buttons
//   PassthroughVitcPublisher :9901 MJPEG for walk-capture (needs a
//                            PassthroughCameraAccess in the scene)
//
// Set `macHost` to your Mac's LAN IP (the machine running
// `python -m integrations.viture.server`). It can also be changed at runtime
// via ReachyBridgeSender.SetHost / PlayerPrefs without a rebuild.

using Meta.XR;
using UnityEngine;

namespace ReachyBridge
{
    public class ReachyBridgeBootstrap : MonoBehaviour
    {
        [Tooltip("Mac LAN IP running integrations/viture/server.py.")]
        public string macHost = "192.168.0.217";
        [Tooltip("Also publish the passthrough camera for walk-capture (needs a " +
                 "PassthroughCameraAccess in the scene, e.g. from the CameraViewer sample).")]
        public bool enableCameraPublisher = true;

        void Awake()
        {
            var pointer = GetOrAdd<QuestPointerProvider>();

            var sender = GetOrAdd<ReachyBridgeSender>();
            sender.pointer = pointer;
            if (!string.IsNullOrWhiteSpace(macHost)) sender.host = macHost.Trim();

            var controls = GetOrAdd<ReachyBridgeControls>();
            controls.sender = sender;

            var hud = GetOrAdd<ReachyBridgeHud>();
            hud.sender = sender;
            hud.pointer = pointer;
            controls.hud = hud;

            if (enableCameraPublisher)
            {
                var pub = GetOrAdd<PassthroughVitcPublisher>();
                if (pub.cameraAccess == null)
                    pub.cameraAccess = FindObjectOfType<PassthroughCameraAccess>();
                if (pub.cameraAccess == null)
                    Debug.LogWarning("[ReachyBridge] No PassthroughCameraAccess in the scene — " +
                                     "walk-capture (B) will do nothing. Add one (see the CameraViewer sample) " +
                                     "or clear 'enableCameraPublisher'.");
            }

            Debug.Log($"[ReachyBridge] bootstrapped — server host {sender.host}:{sender.port}");
        }

        T GetOrAdd<T>() where T : Component
        {
            var c = GetComponent<T>();
            return c != null ? c : gameObject.AddComponent<T>();
        }
    }
}
