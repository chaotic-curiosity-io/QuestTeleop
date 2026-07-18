// ReachyBrain XR bridge — Quest port. Touch-controller bindings.
//
// The Viture sender drove modes/calibration from the Mac keyboard (0-4, C, M,
// R, B, X). The Quest has no keyboard, so this maps the same actions to the
// Touch controllers. Hand-tracking users get the equivalent buttons on the
// ReachyBridgeHud panel; both call the same ReachyBridgeSender methods.
//
//   Right controller                Left controller
//     A   gaze capture (C)            X   map-calibrate (M)
//     B   cycle mode                  Y   robot-locate (R)
//     R-stick click  idle/stop        L-stick click  walk-capture toggle (B)
//                                      Menu  toggle HUD panel
//     hold B (>1s)  clear captures (X)
//
// Cycle order for B: idle -> follow_user -> follow_hand -> raycast -> calibrate.

using UnityEngine;

namespace ReachyBridge
{
    public class ReachyBridgeControls : MonoBehaviour
    {
        public ReachyBridgeSender sender;
        public ReachyBridgeHud hud;

        [Tooltip("Seconds to hold B before it clears the gaze calibration captures.")]
        public float clearHoldSeconds = 1.0f;

        float _bHeldSince = -1f;
        bool _bClearedThisHold;

        void Start()
        {
            if (sender == null) sender = FindObjectOfType<ReachyBridgeSender>();
            if (hud == null) hud = FindObjectOfType<ReachyBridgeHud>();
        }

        void Update()
        {
            if (sender == null) return;

            // --- right controller ---
            if (OVRInput.GetDown(OVRInput.RawButton.A)) sender.SendCalibrate("capture");

            // B: tap = cycle mode, hold = clear captures.
            if (OVRInput.GetDown(OVRInput.RawButton.B))
            {
                _bHeldSince = Time.unscaledTime;
                _bClearedThisHold = false;
            }
            if (OVRInput.Get(OVRInput.RawButton.B) && _bHeldSince >= 0f && !_bClearedThisHold
                && Time.unscaledTime - _bHeldSince >= clearHoldSeconds)
            {
                sender.SendCalibrate("reset");
                _bClearedThisHold = true;
            }
            if (OVRInput.GetUp(OVRInput.RawButton.B))
            {
                if (!_bClearedThisHold) sender.CycleMode();
                _bHeldSince = -1f;
            }

            if (OVRInput.GetDown(OVRInput.RawButton.RThumbstick)) sender.SendMode("idle");

            // --- left controller ---
            if (OVRInput.GetDown(OVRInput.RawButton.X)) sender.SendCalibrate("map");
            if (OVRInput.GetDown(OVRInput.RawButton.Y)) sender.SendCalibrate("robot");
            if (OVRInput.GetDown(OVRInput.RawButton.LThumbstick)) sender.SendMapCapture("toggle");
            if (OVRInput.GetDown(OVRInput.RawButton.Start) && hud != null) hud.Toggle();
        }
    }
}
