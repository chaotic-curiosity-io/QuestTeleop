// Headless (Unity CLI) test of the VR-side anchoring — proves the robot ghost
// and its point cloud stay REGISTERED while the operator turns, instead of
// swinging away at a multiple of the head rotation:
//
//   VLA_YAW_DIR=<...>/media/_headless_teleop_yaw/g1_dex3 \
//   unity run ./QuestTeleop --editor-version 6000.3.19f1 --timeout 1200 -- \
//       -executeMethod VlaTeleop.EditorTools.VLATeleopAnchorHeadlessTest.Run
//
// Input is the DevVLA harness's "lock" capture (VLATeleopYawHeadlessTest):
// real VLAD packets recorded while the operator turned their body but held
// their gaze on a fixed target, plus the waist angle the server commanded at
// each step. Replaying them here exercises the exact live path — ghost FK
// from the joint-target echo, RobotPointCloudOverlay ghost-anchored to the
// ghost root — with the head transform standing in for the headset.
//
// Run twice: followPlayer ON (the shipped Superimposed behaviour, which
// re-solved the ghost root from your live pose every frame) and OFF (the fix,
// which anchors once because the robot it mirrors has an immovable base).
// Scored by where a fixed physical point — the target at the image centre —
// lands in VR world space across the sweep. Correctly registered, it does not
// move; double-counted, it swings around you.
//
// Exits 0 only if the anchored run holds the point still AND the following run
// reproduces the drift, so the harness is provably sensitive to the bug.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class VLATeleopAnchorHeadlessTest
    {
        const int HeaderSize = 72;
        const int RenderW = 640, RenderH = 480;

        [Serializable]
        class LockFrame
        {
            public float body_yaw_deg;
            public float waist_rad;
            public string packet;
        }

        [Serializable]
        class YawReport
        {
            public string robot;
            public string waist_joint;
            public float waist_limit_deg;
            public LockFrame[] lock_frames;
        }

        public static void Run()
        {
            try
            {
                RunInner();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VLATeleopAnchor] FAILED: {e}");
                EditorApplication.Exit(1);
            }
        }

        static string Env(string k, string d)
        {
            string v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? d : v;
        }

        static void RunInner()
        {
            string yawDir = Env("VLA_YAW_DIR", "");
            if (string.IsNullOrEmpty(yawDir) || !Directory.Exists(yawDir))
                throw new Exception($"VLA_YAW_DIR '{yawDir}' not found — run the DevVLA " +
                                    "harness (VLATeleopYawHeadlessTest) first");
            string reportPath = Path.Combine(yawDir, "yaw_report.json");
            if (!File.Exists(reportPath)) throw new Exception($"{reportPath} missing");
            var report = JsonUtility.FromJson<YawReport>(File.ReadAllText(reportPath));
            if (report == null || report.lock_frames == null || report.lock_frames.Length == 0)
                throw new Exception("yaw_report.json has no lock_frames");

            string outDir = Env("VLA_OUT_DIR", Path.Combine(yawDir, "vr_anchor"));
            Directory.CreateDirectory(outDir);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sun.intensity = 1.1f;

            BuildGhost(report.robot);
            var ghost = UnityEngine.Object.FindObjectOfType<RobotOverlayGhost>()
                ?? throw new Exception($"no RobotOverlayGhost after building '{report.robot}'");
            Debug.Log($"[VLATeleopAnchor] ghost '{ghost.name}' ({ghost.joints.Length} joints), " +
                      $"{report.lock_frames.Length} lock frames, waist '{report.waist_joint}'.");

            // Stand-ins for the headset rig: the head the operator turns, and
            // the tracking-space floor the ghost's feet rest on.
            var head = new GameObject("PlayerHead").transform;
            head.position = new Vector3(0f, 1.60f, 0f);
            var floor = new GameObject("TrackingSpace").transform;
            floor.position = Vector3.zero;

            var host = new GameObject("RobotOverlay");
            var driver = host.AddComponent<RobotOverlayDriver>();
            driver.ghost = ghost;
            driver.headTransform = head;
            driver.trackingSpace = floor;
            driver.bodyAnchors = null;              // head-based anchoring (no OVR here)
            driver.mode = RobotOverlayDriver.AnchorMode.Superimposed;
            driver.showHud = false;
            driver.heartbeatSeconds = 0f;

            var overlay = host.AddComponent<RobotPointCloudOverlay>();
            overlay.mode = RobotPointCloudOverlay.AnchorMode.GhostAnchored;
            overlay.ghost = ghost;
            overlay.style = RobotPointCloudOverlay.RenderStyle.Points;
            overlay.pointSize = 0.02f;
            overlay.showHud = false;
            overlay.heartbeatSeconds = 0f;

            var cam = new GameObject("RenderCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.10f);
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;

            var runs = new Dictionary<string, List<Frame>>();
            foreach (bool follow in new[] { true, false })
            {
                string tag = follow ? "follow-legacy" : "anchored-fixed";
                runs[tag] = Replay(report, yawDir, driver, ghost, overlay, head, cam,
                                   follow, Path.Combine(outDir, tag));
            }

            var csv = new StringBuilder("mode,body_yaw_deg,ghost_yaw_deg,reg_x,reg_y,reg_z," +
                                        "reg_drift_m,cloud_x,cloud_y,cloud_z,cloud_drift_m\n");
            var regDrift = new Dictionary<string, float>();
            var cloudDrift = new Dictionary<string, float>();
            foreach (var kv in runs)
            {
                Vector3 reg0 = kv.Value[0].registered, cloud0 = kv.Value[0].cloudPoint;
                float maxReg = 0f, maxCloud = 0f;
                foreach (var s in kv.Value)
                {
                    float dr = Vector3.Distance(s.registered, reg0);
                    float dc = Vector3.Distance(s.cloudPoint, cloud0);
                    maxReg = Mathf.Max(maxReg, dr);
                    maxCloud = Mathf.Max(maxCloud, dc);
                    csv.Append($"{kv.Key},{F(s.theta)},{F(s.ghostYaw)},{F(s.registered.x)}," +
                               $"{F(s.registered.y)},{F(s.registered.z)},{F(dr)}," +
                               $"{F(s.cloudPoint.x)},{F(s.cloudPoint.y)},{F(s.cloudPoint.z)},{F(dc)}\n");
                }
                regDrift[kv.Key] = maxReg;
                cloudDrift[kv.Key] = maxCloud;
            }
            File.WriteAllText(Path.Combine(outDir, "anchor_drift.csv"), csv.ToString());

            float regLegacy = regDrift["follow-legacy"], regFixed = regDrift["anchored-fixed"];
            float cloudLegacy = cloudDrift["follow-legacy"], cloudFixed = cloudDrift["anchored-fixed"];
            float yawGainLegacy = YawGain(runs["follow-legacy"]);
            float yawGainFixed = YawGain(runs["anchored-fixed"]);
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append($"  \"robot\": \"{report.robot}\",\n");
            json.Append($"  \"frames\": {report.lock_frames.Length},\n");
            json.Append($"  \"registration_drift_m_follow_legacy\": {F(regLegacy)},\n");
            json.Append($"  \"registration_drift_m_anchored_fixed\": {F(regFixed)},\n");
            json.Append($"  \"cloud_point_drift_m_follow_legacy\": {F(cloudLegacy)},\n");
            json.Append($"  \"cloud_point_drift_m_anchored_fixed\": {F(cloudFixed)},\n");
            json.Append($"  \"ghost_root_yaw_gain_follow_legacy\": {F(yawGainLegacy)},\n");
            json.Append($"  \"ghost_root_yaw_gain_anchored_fixed\": {F(yawGainFixed)}\n");
            json.Append("}\n");
            File.WriteAllText(Path.Combine(outDir, "anchor_report.json"), json.ToString());

            Debug.Log($"[VLATeleopAnchor] registration drift (a FIXED point of the robot's world, " +
                      $"in VR space): legacy(followPlayer) {regLegacy:0.000} m vs " +
                      $"fixed(anchored) {regFixed:0.000} m. Centre-ray point (includes real " +
                      $"parallax from the camera orbiting on the waist): {cloudLegacy:0.000} vs " +
                      $"{cloudFixed:0.000} m. Ghost root yaw gain {yawGainLegacy:0.00} vs " +
                      $"{yawGainFixed:0.00} -> {outDir}");

            if (regLegacy < 0.5f)
                throw new Exception($"legacy registration drift {regLegacy:0.000} m — harness is " +
                                    "not reproducing the anchor double-count it should catch");
            if (regFixed > 0.01f)
                throw new Exception($"anchored registration drift {regFixed:0.000} m > 0.01 — the " +
                                    "robot's world is still sliding around in VR space");
            if (cloudFixed > 0.25f)
                throw new Exception($"anchored centre-ray drift {cloudFixed:0.000} m > 0.25 — more " +
                                    "than the waist-orbit parallax can explain");
            if (Mathf.Abs(yawGainFixed) > 0.05f)
                throw new Exception($"anchored ghost root still yaws with the player " +
                                    $"(gain {yawGainFixed:0.00})");
            Debug.Log($"[VLATeleopAnchor] OK — {report.robot}");
        }

        sealed class Frame
        {
            public float theta;             // operator body yaw
            public float ghostYaw;          // ghost root yaw in VR world
            public Vector3 registered;      // frame 0's scene point, re-mapped each frame
            public Vector3 cloudPoint;      // this frame's centre-ray point
        }

        /// <summary>Replay the captured sweep once, with the ghost root either
        /// chasing the player (legacy) or anchored (fixed).
        ///
        /// Two different questions, two numbers. `cloudPoint` is where THIS
        /// frame's centre ray lands — it moves a little even when everything is
        /// right, because the camera physically orbits the waist axis and so
        /// sees past the target from a new position (real parallax).
        /// `registered` re-maps frame 0's scene point (fixed in the robot's base
        /// frame) through the current ghost root, so it isolates the anchoring:
        /// a planted root leaves it exactly where it was.</summary>
        static List<Frame> Replay(
            YawReport report, string yawDir, RobotOverlayDriver driver, RobotOverlayGhost ghost,
            RobotPointCloudOverlay overlay, Transform head, Camera cam, bool follow, string frameDir)
        {
            Directory.CreateDirectory(frameDir);
            driver.followPlayer = follow;
            driver.Recenter();                       // fresh anchor for this run
            head.rotation = Quaternion.identity;
            ghost.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var rt = new RenderTexture(RenderW, RenderH, 24, RenderTextureFormat.ARGB32)
            { antiAliasing = 2 };
            var tex = new Texture2D(RenderW, RenderH, TextureFormat.RGB24, false);
            var results = new List<Frame>();
            Vector3 basePoint0 = Vector3.zero;      // frame 0's scene point, in ROBOT BASE frame
            bool haveBase0 = false;

            foreach (var f in report.lock_frames)
            {
                // The operator turns: head yaw is the only pose signal this
                // component gets without body tracking.
                head.rotation = Quaternion.Euler(0f, f.body_yaw_deg, 0f);
                for (int i = 0; i < 6; i++) driver.StepAnchor(1f);   // converge the lerp

                // The waist target the server echoed for this frame.
                ghost.SetTargets(new[] { report.waist_joint }, new[] { f.waist_rad });

                byte[] packet = File.ReadAllBytes(Path.Combine(yawDir, "depth_packets", f.packet));
                Transform cloud = overlay.ApplyPacketDirect(packet, ghost.transform)
                    ?? throw new Exception($"overlay rejected packet {f.packet}");

                // The scene point at the image centre: straight down the optical
                // axis (+Z of the cloud transform, cf. RobotPointCloud shaders)
                // at the centre pixel's depth.
                float d = CentreDepth(packet);
                if (d <= 0f) throw new Exception($"no valid centre depth in {f.packet}");
                Vector3 basePoint = cloud.localPosition + cloud.localRotation * new Vector3(0f, 0f, d);
                if (!haveBase0) { basePoint0 = basePoint; haveBase0 = true; }

                results.Add(new Frame
                {
                    theta = f.body_yaw_deg,
                    ghostYaw = Wrap(ghost.transform.eulerAngles.y),
                    registered = ghost.transform.TransformPoint(basePoint0),
                    cloudPoint = cloud.TransformPoint(new Vector3(0f, 0f, d)),
                });

                // Fixed third-person view of you + ghost + cloud.
                cam.transform.position = new Vector3(3.2f, 2.4f, -3.2f);
                cam.transform.LookAt(new Vector3(0f, 1.0f, 0.6f));
                RenderTexture prevT = cam.targetTexture, prevA = RenderTexture.active;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, RenderW, RenderH), 0, 0);
                tex.Apply(false);
                cam.targetTexture = prevT;
                RenderTexture.active = prevA;
                File.WriteAllBytes(
                    Path.Combine(frameDir, $"yaw_{Mathf.RoundToInt(f.body_yaw_deg):000}.png"),
                    tex.EncodeToPNG());
            }

            rt.Release();
            return results;
        }

        /// <summary>Least-squares slope of ghost root yaw vs operator body yaw.
        /// 1.0 = the root chases you (and the waist joint then adds your yaw a
        /// second time); 0.0 = the root is planted, like the real robot's
        /// immovable base.</summary>
        static float YawGain(List<Frame> rows)
        {
            if (rows.Count < 2) return float.NaN;
            float y0 = rows[0].ghostYaw;
            double n = rows.Count;
            double sx = rows.Sum(r => (double)r.theta);
            double sy = rows.Sum(r => (double)Mathf.DeltaAngle(y0, r.ghostYaw));
            double sxx = rows.Sum(r => (double)r.theta * r.theta);
            double sxy = rows.Sum(r => (double)r.theta * Mathf.DeltaAngle(y0, r.ghostYaw));
            return (float)((n * sxy - sx * sy) / (n * sxx - sx * sx));
        }

        /// <summary>Depth (metres) at the image centre, searching outwards a few
        /// pixels if the exact centre happens to be invalid.</summary>
        static float CentreDepth(byte[] p)
        {
            int w = p[12] | p[13] << 8, h = p[14] | p[15] << 8;
            for (int r = 0; r < 8; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = w / 2 + dx, y = h / 2 + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int i = y * w + x;
                        int mm = p[HeaderSize + i * 2] | (p[HeaderSize + i * 2 + 1] << 8);
                        if (mm > 0) return mm * 0.001f;
                    }
                }
            }
            return 0f;
        }

        static void BuildGhost(string robot)
        {
            switch (robot)
            {
                case "h1": RobotOverlayBuilder.BuildH1Ghost(); break;
                case "gr1": RobotOverlayBuilder.BuildGr1Ghost(); break;
                case "g1":
                case "g1_dex3": RobotOverlayBuilder.BuildG1Ghost(); break;
                default: throw new Exception($"no ghost builder for robot '{robot}'");
            }
        }

        static float Wrap(float deg) => Mathf.Repeat(deg + 180f, 360f) - 180f;

        static string F(float v) =>
            (float.IsNaN(v) ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);
    }
}
