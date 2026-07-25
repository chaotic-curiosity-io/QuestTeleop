// Headless (Unity CLI) verification that the QuestTeleop VR side can receive a
// VLAD depth packet and generate the point cloud — WITHOUT a headset — and a
// GAME-VIEW MP4 of the result for review. Proves the receive+unproject+color
// path (RobotPointCloudOverlay.ApplyPacketDirect, which shares DecodeAndUpload
// with the live UDP path, + the built-in-RP RobotPointCloud[Points] vertex
// shader) by rendering the reconstructed 3D cloud.
//
//   VLA_PKT_DIR=<...>/depth_packets VLA_RGB_DIR=<...>/rgb \
//   VLA_OUT_DIR=<...>/quest_render VLA_STYLE=points|surface \
//   unity run "<QuestTeleop>" --editor-version 6000.3.19f1 --timeout 1200 -- \
//       -executeMethod VlaTeleop.EditorTools.VLADepthOverlayHeadlessTest.Run
//
// It ORBITS a camera around the cloud reconstructed from ONE representative
// packet (so the parallax proves the geometry is truly 3D, not a flat image),
// writing frames/orbit_NNN.png, then advances through the whole capture from a
// fixed 3/4 view (frames/stream_NNN.png). ffmpeg (run by the caller) turns
// either sequence into an mp4. A Quest headset running the same component
// against a live :9909 stream renders the identical cloud — the only
// difference is transport (a background UDP thread) and anchor (ghost/head).
//
// Exits 0 only if the rendered cloud contains a non-trivial number of lit
// (non-background) pixels — i.e. the shader actually placed geometry.

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class VLADepthOverlayHeadlessTest
    {
        public static void Run()
        {
            try
            {
                RunInner();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VLADepthOverlayHeadlessTest] FAILED: {e}");
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
            string pktDir = Env("VLA_PKT_DIR", "");
            string rgbDir = Env("VLA_RGB_DIR", "");
            string outDir = Env("VLA_OUT_DIR",
                Path.GetFullPath(Path.Combine(Application.dataPath, "../quest_render")));
            string styleStr = Env("VLA_STYLE", "points").ToLowerInvariant();
            if (string.IsNullOrEmpty(pktDir) || !Directory.Exists(pktDir))
                throw new Exception($"VLA_PKT_DIR '{pktDir}' not found");
            string framesDir = Path.Combine(outDir, "frames");
            Directory.CreateDirectory(framesDir);

            var packets = Directory.GetFiles(pktDir, "*.vlad.bin").OrderBy(p => p).ToArray();
            if (packets.Length == 0) throw new Exception($"no .vlad.bin in {pktDir}");

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // The overlay component, added in edit mode: Start() (UDP bind +
            // thread + OVR lookup) never fires, so we drive it purely through
            // the public ApplyPacketDirect entry.
            var host = new GameObject("PointCloudOverlayHost");
            var overlay = host.AddComponent<RobotPointCloudOverlay>();
            overlay.style = styleStr == "surface"
                ? RobotPointCloudOverlay.RenderStyle.Surface
                : RobotPointCloudOverlay.RenderStyle.Points;
            overlay.pointSize = 0.02f;
            overlay.tearFraction = 0.06f;
            overlay.colormapRange = 6f;

            // Robot base = world origin; the cloud parents here at the camera
            // pose the packet carries (same as GhostAnchored would do around a
            // ghost whose root is the base link).
            var baseRoot = new GameObject("RobotBase").transform;

            var camGo = new GameObject("RenderCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f, 1f);
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 60f;

            const int rw = 960, rh = 540;
            var rt = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var tex = new Texture2D(rw, rh, TextureFormat.RGB24, false);

            // The point of interest: the captured scene sits roughly in front of
            // the robot base (Unity forward +Z here) around torso height.
            Vector3 look = new Vector3(0f, 0.8f, 1.6f);
            float orbitR = 3.4f, orbitH = 1.9f;

            // --- Pass 1: ORBIT one representative frame (parallax => proves 3D).
            int mid = packets.Length / 3;
            Texture2D rgbMid = LoadRgb(rgbDir, packets[mid]);
            if (overlay.ApplyPacketDirect(File.ReadAllBytes(packets[mid]), baseRoot, rgbMid) == null)
                throw new Exception("ApplyPacketDirect returned null (orbit frame)");
            int orbitN = 72, litMin = int.MaxValue;
            for (int i = 0; i < orbitN; i++)
            {
                float a = i / (float)orbitN * Mathf.PI * 2f;
                camGo.transform.position = look + new Vector3(Mathf.Sin(a) * orbitR, orbitH - look.y,
                                                              -Mathf.Cos(a) * orbitR);
                camGo.transform.LookAt(look);
                int lit = RenderTo(cam, rt, tex, Path.Combine(framesDir, $"orbit_{i:000}.png"));
                litMin = Mathf.Min(litMin, lit);
            }
            if (rgbMid != null) UnityEngine.Object.DestroyImmediate(rgbMid);
            if (litMin < rw * rh / 400)
                throw new Exception($"orbit cloud rendered nearly empty (min {litMin} lit px)");

            // --- Pass 2: STREAM the whole capture from a fixed 3/4 view.
            camGo.transform.position = look + new Vector3(-2.6f, orbitH - look.y, -2.6f);
            camGo.transform.LookAt(look);
            int litSum = 0;
            for (int i = 0; i < packets.Length; i++)
            {
                Texture2D rgb = LoadRgb(rgbDir, packets[i]);
                overlay.ApplyPacketDirect(File.ReadAllBytes(packets[i]), baseRoot, rgb);
                litSum += RenderTo(cam, rt, tex, Path.Combine(framesDir, $"stream_{i:000}.png"));
                if (rgb != null) UnityEngine.Object.DestroyImmediate(rgb);
            }

            rt.Release();
            Debug.Log($"[VLADepthOverlayHeadlessTest] OK — style={styleStr}, orbit {orbitN} + " +
                      $"stream {packets.Length} frames, mean stream lit " +
                      $"{100f * litSum / packets.Length / (rw * rh):0.0}% -> {framesDir}. " +
                      "The VR-side receive+unproject+color path works headlessly.");
        }

        static Texture2D LoadRgb(string rgbDir, string pktPath)
        {
            if (string.IsNullOrEmpty(rgbDir)) return null;
            string stem = Path.GetFileName(pktPath).Split('.')[0];
            string rgbPath = Path.Combine(rgbDir, stem + ".png");
            if (!File.Exists(rgbPath)) return null;
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(rgbPath));
            return t;
        }

        static int RenderTo(Camera cam, RenderTexture rt, Texture2D tex, string path)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            cam.targetTexture = null;
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            return CountLitPixels(tex, cam.backgroundColor);
        }

        static int CountLitPixels(Texture2D tex, Color bg)
        {
            var px = tex.GetPixels32();
            byte br = (byte)(bg.r * 255), bgc = (byte)(bg.g * 255), bb = (byte)(bg.b * 255);
            int lit = 0;
            foreach (var c in px)
                if (Mathf.Abs(c.r - br) > 12 || Mathf.Abs(c.g - bgc) > 12 || Mathf.Abs(c.b - bb) > 12)
                    lit++;
            return lit;
        }
    }
}
