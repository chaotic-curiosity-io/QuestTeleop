// VLA XR teleop — Editor menu to start/stop the Python teleop server, so a
// Play-mode session never needs a hand-opened terminal.
//
//   Tools/Robot Teleop/Start Teleop Server (Quest → H1)
//       Launches handtracking/teleop_server.py --robot h1 --source quest-xr
//       in its own console window (the window IS the live heartbeat log).
//       Needs `uv` on PATH; deps (numpy/websockets) resolve automatically.
//   Tools/Robot Teleop/Stop Teleop Server
//       Kills the launched process tree (taskkill /T).
//   Tools/Robot Teleop/Open Server Logs Folder
//       The per-session JSONL stage logs (handtracking/logs/).
//   Tools/Robot Teleop/Set Handtracking Folder…
//       Where the openvla-unity-sim2real handtracking/ dir lives (EditorPrefs).
//
// The PID survives domain reloads via SessionState, so Stop works even after
// entering/exiting Play mode.

using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VlaTeleop.EditorTools
{
    public static class TeleopServerMenu
    {
        const string PathPref = "VlaTeleop.HandtrackingPath";
        const string PidKey = "VlaTeleop.ServerPid";
        const string DefaultPath =
            @"C:\Users\alire\Documents\Projects\robotics-unity\handtracking";

        static string HandtrackingPath => EditorPrefs.GetString(PathPref, DefaultPath);

        [MenuItem("Tools/Robot Teleop/Start Teleop Server (Quest → H1)", priority = 40)]
        public static void StartServer() => StartServer("h1");

        [MenuItem("Tools/Robot Teleop/Start Teleop Server (Quest → GR-1)", priority = 41)]
        public static void StartServerGr1() => StartServer("gr1");

        [MenuItem("Tools/Robot Teleop/Start Teleop Server (Quest → G1)", priority = 42)]
        public static void StartServerG1() => StartServer("g1_dex3");

        static void StartServer(string robot)
        {
            if (RunningPid() > 0)
            {
                Debug.LogWarning($"[VlaTeleop] Teleop server already running "
                                 + $"(pid {RunningPid()}). Stop it first.");
                return;
            }
            string dir = HandtrackingPath;
            if (!File.Exists(Path.Combine(dir, "teleop_server.py")))
            {
                EditorUtility.DisplayDialog(
                    "handtracking folder not found",
                    $"teleop_server.py not found in:\n{dir}\n\nUse Tools ▸ Robot "
                    + "Teleop ▸ Set Handtracking Folder… to point at the "
                    + "openvla-unity-sim2real handtracking/ directory.",
                    "OK");
                return;
            }

            // --record-raw ARMS the raw-episode recorder (does not start it):
            // a double pinch in VR toggles recording; episodes land under
            // handtracking/episodes/ as teleop_raw_v1 for the fine-tune
            // pipeline (retarget_episode.py -> episode_quality.py -> LeRobot).
            string args = $"--robot {robot} --source quest-xr --record-raw episodes";
            // cmd /k keeps the console open (it IS the live heartbeat log, and
            // errors stay readable after exit).
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k uv run --no-project --with numpy --with websockets "
                            + $"python teleop_server.py {args}",
                WorkingDirectory = dir,
                UseShellExecute = true,
            };
            try
            {
                var proc = Process.Start(psi);
                SessionState.SetInt(PidKey, proc.Id);
                Debug.Log($"[VlaTeleop] Teleop server console started (pid {proc.Id}): "
                          + $"teleop_server.py {args}  [{dir}]\n"
                          + "WS :8766 for GrootBridge, UDP :9905 for this app. "
                          + "Stop via Tools ▸ Robot Teleop ▸ Stop Teleop Server.");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[VlaTeleop] Could not start the server ({e.Message}). "
                               + "Is `uv` installed and on PATH?");
            }
        }

        [MenuItem("Tools/Robot Teleop/Stop Teleop Server", priority = 42)]
        public static void StopServer()
        {
            int pid = RunningPid();
            if (pid <= 0)
            {
                Debug.Log("[VlaTeleop] No teleop server tracked by this session.");
                return;
            }
            // Kill the whole tree: cmd -> uv -> python.
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.WaitForExit(5000);
            SessionState.EraseInt(PidKey);
            Debug.Log($"[VlaTeleop] Teleop server (pid {pid}) stopped.");
        }

        [MenuItem("Tools/Robot Teleop/Stop Teleop Server", validate = true)]
        static bool ValidateStop() => RunningPid() > 0;

        [MenuItem("Tools/Robot Teleop/Open Server Logs Folder", priority = 43)]
        public static void OpenLogs()
        {
            string logs = Path.Combine(HandtrackingPath, "logs");
            Directory.CreateDirectory(logs);
            EditorUtility.RevealInFinder(logs);
        }

        [MenuItem("Tools/Robot Teleop/Set Handtracking Folder…", priority = 44)]
        public static void SetPath()
        {
            string picked = EditorUtility.OpenFolderPanel(
                "openvla-unity-sim2real handtracking/ folder", HandtrackingPath, "");
            if (string.IsNullOrEmpty(picked)) return;
            EditorPrefs.SetString(PathPref, picked);
            Debug.Log($"[VlaTeleop] Handtracking folder set: {picked}");
        }

        static int RunningPid()
        {
            int pid = SessionState.GetInt(PidKey, 0);
            if (pid <= 0) return 0;
            try
            {
                var p = Process.GetProcessById(pid);
                return p.HasExited ? 0 : pid;
            }
            catch (System.Exception)
            {
                return 0;                     // exited / recycled pid
            }
        }
    }
}
