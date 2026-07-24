// VLA XR teleop — floating world-space HUD panel.
//
// The OnGUI HUDs (VlaTeleopSender / RobotOverlayDriver) only render to the
// flat mirror view on the PC — they are INVISIBLE inside the headset. This
// panel is the in-VR equivalent: a lazy-follow world-space canvas that
// aggregates both ends of the loop plus gesture-debug detail:
//
//   send:  packet count + achieved Hz, per-hand tracking/conf, body/legs,
//          teleop mode (and how to cycle it), last socket error
//   echo:  robot, applied joints, echo Hz, seq-gap drops, staleness,
//          server-honored mode, red REC + frame count while recording
//   hands: live index/middle pinch strengths (the two control gestures)
//
// Add via Tools ▸ Robot Teleop ▸ Add Floating HUD Panel, or drop the
// component on any GameObject (it builds its own canvas). Toggle at runtime
// with the `visible` checkbox. Rich-text UI.Text on a world-space canvas —
// no TextMeshPro dependency, billboard + exponential lazy-follow like the
// robot ghost's root anchor.

using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace VlaTeleop
{
    [DisallowMultipleComponent]
    public class VlaTeleopHudPanel : MonoBehaviour
    {
        [Header("Sources (auto-found if left empty)")]
        public VlaTeleopSender sender;
        public RobotOverlayDriver overlay;
        [Tooltip("Head pose source. Auto: OVRCameraRig.centerEyeAnchor, else Camera.main.")]
        public Transform headTransform;

        [Header("Panel")]
        public bool visible = true;
        [Tooltip("Meters in front of the head.")]
        public float distance = 0.9f;
        [Tooltip("Degrees below the forward eye line.")]
        public float pitchDown = 18f;
        [Tooltip("Re-aim rate (1/s). Low = lazy follow, panel stays readable.")]
        public float followLerp = 3f;
        [Range(6, 22)] public int fontSize = 12;

        Canvas _canvas;
        Text _text;
        float _lastSent;
        float _rateWindow;
        int _sendRate;

        void Start()
        {
            if (sender == null) sender = FindObjectOfType<VlaTeleopSender>();
            if (overlay == null) overlay = FindObjectOfType<RobotOverlayDriver>();
            var rig = FindObjectOfType<OVRCameraRig>();
            if (headTransform == null && rig != null) headTransform = rig.centerEyeAnchor;
            if (headTransform == null && Camera.main != null) headTransform = Camera.main.transform;
            BuildCanvas();
        }

        void BuildCanvas()
        {
            var go = new GameObject("VlaTeleopHudCanvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvas.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(420, 210);
            go.transform.localScale = Vector3.one * 0.001f;   // 0.42 m wide

            var bg = new GameObject("bg").AddComponent<Image>();
            bg.transform.SetParent(go.transform, false);
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            Stretch(bg.rectTransform);

            _text = new GameObject("text").AddComponent<Text>();
            _text.transform.SetParent(go.transform, false);
            Stretch(_text.rectTransform, 10f);
            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch { }
            }
            _text.font = font;
            _text.fontSize = fontSize;
            _text.supportRichText = true;
            _text.color = new Color(0.85f, 0.95f, 1f);
            _text.alignment = TextAnchor.UpperLeft;
        }

        static void Stretch(RectTransform rt, float pad = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        void LateUpdate()
        {
            if (_canvas == null) return;
            _canvas.enabled = visible;
            if (!visible || headTransform == null) return;

            // Lazy follow: aim point ahead of + below the eye line, billboarded.
            Quaternion aim = headTransform.rotation
                             * Quaternion.Euler(pitchDown, 0f, 0f);
            Vector3 targetPos = headTransform.position
                                + aim * Vector3.forward * distance;
            float k = 1f - Mathf.Exp(-followLerp * Time.deltaTime);
            var t = _canvas.transform;
            t.position = Vector3.Lerp(t.position, targetPos, k);
            t.rotation = Quaternion.Slerp(
                t.rotation,
                Quaternion.LookRotation(t.position - headTransform.position),
                k);

            // Achieved send rate over a 1 s bucket.
            if (sender != null && Time.unscaledTime - _rateWindow >= 1f)
            {
                _sendRate = Mathf.RoundToInt(sender.SentCount - _lastSent);
                _lastSent = sender.SentCount;
                _rateWindow = Time.unscaledTime;
            }
            _text.text = Compose();
        }

        string Compose()
        {
            var sb = new StringBuilder(512);
            if (sender != null)
            {
                var p = sender.LastPacket;
                sb.Append("<b>SEND</b>  ").Append(sender.SentCount)
                  .Append(" pkts  ").Append(_sendRate).Append(" Hz  mode:")
                  .Append(sender.mode).Append('\n');
                sb.Append("hands  L:").Append(HandCell(p.left))
                  .Append("  R:").Append(HandCell(p.right)).Append('\n');
                sb.Append("body ").Append(p.body.valid ? "<color=#7fff7f>✓</color>" : "–")
                  .Append("  legs ").Append(p.body.legs_valid ? "<color=#7fff7f>✓</color>" : "–");
                if (sender.LastSendError != null)
                    sb.Append("  <color=#ff8080>send:")
                      .Append(sender.LastSendError).Append("</color>");
                sb.Append('\n');
                if (sender.leftHand != null || sender.rightHand != null)
                {
                    sb.Append("pinch  idx ").Append(Pinch(sender.leftHand, OVRHand.HandFinger.Index))
                      .Append('/').Append(Pinch(sender.rightHand, OVRHand.HandFinger.Index))
                      .Append("  mid ").Append(Pinch(sender.leftHand, OVRHand.HandFinger.Middle))
                      .Append('/').Append(Pinch(sender.rightHand, OVRHand.HandFinger.Middle))
                      .Append("   <i>2×idx=rec  2×mid=mode</i>\n");
                }
            }
            sb.Append('\n');
            if (overlay == null)
            {
                sb.Append("<b>ECHO</b>  no RobotOverlayDriver in scene");
            }
            else if (!overlay.HasEcho)
            {
                sb.Append("<b>ECHO</b>  waiting for joint_targets…");
            }
            else
            {
                sb.Append("<b>ECHO</b>  ").Append(overlay.Robot)
                  .Append("  joints:").Append(overlay.AppliedJoints)
                  .Append("  ").Append(overlay.EchoRate).Append(" Hz");
                if (overlay.Drops > 0) sb.Append("  drop:").Append(overlay.Drops);
                if (overlay.ServerMode.Length > 0)
                    sb.Append("  [").Append(overlay.ServerMode).Append(']');
                if (overlay.EchoAge > 0.5f && !float.IsInfinity(overlay.EchoAge))
                    sb.Append("  <color=#ffcc66>STALE ")
                      .Append(overlay.EchoAge.ToString("0.0")).Append("s</color>");
                sb.Append('\n');
                if (overlay.Recording)
                    sb.Append("<color=#ff4040><b>● REC ")
                      .Append(overlay.RecFrames).Append("f</b></color>");
            }
            return sb.ToString();
        }

        static string HandCell(VlaTeleopSender.HandPayload h)
        {
            if (!h.visible) return "–";
            return h.conf >= 0.9f ? "<color=#7fff7f>✓</color>"
                                  : "<color=#ffcc66>~</color>";
        }

        static string Pinch(OVRHand hand, OVRHand.HandFinger finger)
        {
            if (hand == null || !hand.IsTracked) return "–";
            return hand.GetFingerPinchStrength(finger).ToString("0.0");
        }
    }
}
