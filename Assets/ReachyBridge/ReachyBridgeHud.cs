// ReachyBrain XR bridge — Quest port. World-space HUD + control panel.
//
// The Viture sender drew its status/legend with OnGUI, which does not render in
// VR. This builds an equivalent panel as a world-space UGUI canvas at runtime
// (no scene setup, no prefab) and drives its buttons with a small self-contained
// laser: it intersects the active pointer ray with the panel plane and tests
// rect containment, so it needs no EventSystem/OVRInputModule and works with
// both Touch controllers and hand tracking.
//
// Buttons cover every action (mode cycle, gaze capture/clear, map-calibrate,
// robot-locate, walk-capture, anchor source) so hand-only users never need the
// controllers; the status line mirrors the Mac server's xr_status.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ReachyBridge
{
    public class ReachyBridgeHud : MonoBehaviour
    {
        public ReachyBridgeSender sender;
        public QuestPointerProvider pointer;
        public OVRCameraRig rig;

        [Header("Placement")]
        [Tooltip("Meters in front of the head when (re)shown.")]
        public float distance = 0.6f;
        [Tooltip("Vertical offset (m) so the panel sits below eye line.")]
        public float verticalOffset = -0.15f;

        Canvas _canvas;
        Text _status;
        LineRenderer _laser;
        readonly List<(RectTransform rect, Image bg, Action action)> _buttons = new();
        bool _visible = true;
        bool _prevPinch;
        int _hover = -1;

        static readonly Color Idle = new(0.15f, 0.17f, 0.21f, 0.92f);
        static readonly Color Hover = new(0.20f, 0.45f, 0.70f, 0.97f);

        void Start()
        {
            if (sender == null) sender = FindObjectOfType<ReachyBridgeSender>();
            if (pointer == null) pointer = FindObjectOfType<QuestPointerProvider>();
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            BuildPanel();
            Reposition();
        }

        public void Toggle()
        {
            _visible = !_visible;
            _canvas.gameObject.SetActive(_visible);
            _laser.gameObject.SetActive(_visible);
            if (_visible) Reposition();
        }

        void Reposition()
        {
            var head = rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor
                     : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;
            var flatFwd = head.forward; flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = Vector3.forward;
            flatFwd.Normalize();
            transform.position = head.position + flatFwd * distance + Vector3.up * verticalOffset;
            transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
        }

        // ------------------------------------------------------------------ //
        // Panel construction (runtime UGUI, world space)                      //
        // ------------------------------------------------------------------ //

        void BuildPanel()
        {
            var canvasGo = new GameObject("ReachyHudCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvas.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(420, 300);
            // 420 px -> ~0.42 m wide.
            rt.localScale = Vector3.one * 0.001f;
            canvasGo.AddComponent<CanvasScaler>();

            AddBackground(rt, new Color(0.06f, 0.07f, 0.09f, 0.85f));
            _status = AddText(rt, new Vector2(0, 108), new Vector2(400, 80), 15,
                              TextAnchor.UpperLeft, "connecting…");

            // Button grid: 3 columns.
            string[] labels =
            {
                "Cycle Mode", "Gaze Capture", "Clear",
                "Map-Cal (M)", "Robot-Loc (R)", "Walk-Cap (B)",
                "Anchors: Auto", "Anchors: Gaze", "Idle / Stop",
            };
            Action[] actions =
            {
                () => sender.CycleMode(),
                () => sender.SendCalibrate("capture"),
                () => sender.SendCalibrate("reset"),
                () => sender.SendCalibrate("map"),
                () => sender.SendCalibrate("robot"),
                () => sender.SendMapCapture("toggle"),
                () => sender.SendCalibrate("use_auto"),
                () => sender.SendCalibrate("use_gaze"),
                () => sender.SendMode("idle"),
            };
            const int cols = 3;
            var cell = new Vector2(132, 46);
            for (int i = 0; i < labels.Length; i++)
            {
                int c = i % cols, r = i / cols;
                var pos = new Vector2(-134 + c * (cell.x + 4), 40 - r * (cell.y + 6));
                AddButton(rt, pos, cell, labels[i], actions[i]);
            }

            // Laser line.
            var laserGo = new GameObject("ReachyHudLaser");
            laserGo.transform.SetParent(transform, false);
            _laser = laserGo.AddComponent<LineRenderer>();
            _laser.widthMultiplier = 0.004f;
            _laser.positionCount = 2;
            _laser.useWorldSpace = true;
            var mat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            _laser.material = mat;
            _laser.startColor = _laser.endColor = new Color(0.2f, 0.8f, 1f, 0.8f);
        }

        static Image AddBackground(RectTransform parent, Color color)
        {
            var go = new GameObject("bg");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        static Text AddText(RectTransform parent, Vector2 pos, Vector2 size, int fontSize,
                            TextAnchor anchor, string initial)
        {
            var go = new GameObject("text");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                     ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = fontSize;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = initial;
            return t;
        }

        void AddButton(RectTransform parent, Vector2 pos, Vector2 size, string label, Action action)
        {
            var go = new GameObject("btn_" + label);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var bg = go.AddComponent<Image>();
            bg.color = Idle;
            var t = AddText(rt, Vector2.zero, size, 13, TextAnchor.MiddleCenter, label);
            t.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
            _buttons.Add((rt, bg, action));
        }

        // ------------------------------------------------------------------ //
        // Per-frame: status text + laser interaction                          //
        // ------------------------------------------------------------------ //

        void Update()
        {
            if (sender != null && _status != null) _status.text = BuildStatusText();
            if (_visible) UpdateLaser();
        }

        string BuildStatusText()
        {
            string conn = sender.IsConnected ? "<color=#57c08a>server ok</color>"
                                             : $"<color=#e26464>server off ({sender.Host})</color>";
            string robot = sender.HudRobotConnected ? "<color=#57c08a>robot ok</color>"
                                                    : "<color=#e26464>robot off</color>";
            string calib = sender.HudCalibrated ? "<color=#57c08a>calibrated</color>"
                                                : $"<color=#f0c674>{sender.HudCaptures} capture(s)</color>";
            string src = sender.AnchorSource;
            string anchors = string.IsNullOrEmpty(src) ? "<color=#f0c674>anchors: none</color>"
                                                       : $"<color=#57c08a>anchors: {src}</color>";
            string cap = sender.HudMapCapturing ? " · <color=#f0c674>WALK-CAPTURING</color>" : "";
            return $"<b>Reachy</b>  {conn} · {robot}\n" +
                   $"mode <b>{sender.HudMode}</b> · {calib} · {anchors}{cap}\n" +
                   $"<size=12>{sender.HudMessage}</size>";
        }

        void UpdateLaser()
        {
            if (pointer == null || !pointer.TryGetActivePointer(out var hp) || !hp.visible)
            {
                _laser.enabled = false;
                SetHover(-1);
                return;
            }
            _laser.enabled = true;

            // Intersect the ray with the panel plane.
            var plane = new Plane(-transform.forward, transform.position);
            var ray = new Ray(hp.rayOrigin, hp.rayDir);
            float end = 1.2f;
            int hit = -1;
            if (plane.Raycast(ray, out float dist) && dist > 0f && dist < 5f)
            {
                var wp = ray.GetPoint(dist);
                end = dist;
                for (int i = 0; i < _buttons.Count; i++)
                {
                    var lp = _buttons[i].rect.InverseTransformPoint(wp);
                    if (_buttons[i].rect.rect.Contains(lp)) { hit = i; break; }
                }
            }
            _laser.SetPosition(0, hp.rayOrigin);
            _laser.SetPosition(1, ray.GetPoint(end));
            SetHover(hit);

            // Click on pinch release (avoids double-fire while held).
            bool pinch = hp.pinch;
            if (!pinch && _prevPinch && hit >= 0)
                _buttons[hit].action?.Invoke();
            _prevPinch = pinch;
        }

        void SetHover(int idx)
        {
            if (idx == _hover) return;
            if (_hover >= 0 && _hover < _buttons.Count) _buttons[_hover].bg.color = Idle;
            if (idx >= 0 && idx < _buttons.Count) _buttons[idx].bg.color = Hover;
            _hover = idx;
        }
    }
}
