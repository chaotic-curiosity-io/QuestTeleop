// VLA XR teleop — Editor menu to start/stop the Python teleop server, so a
// Play-mode session never needs a hand-opened terminal.
//
//   Tools/Robot Teleop/Start Teleop Server (Quest → H1 / GR-1 / G1)
//       Launches handtracking/teleop_server.py --robot <r> --source quest-xr
//       in its own console window (the window IS the live heartbeat log).
//       Needs `uv`; deps (numpy/websockets) resolve automatically.
//   Tools/Robot Teleop/Stop Teleop Server
//       Kills the launched server.
//   Tools/Robot Teleop/Open Server Logs Folder
//       The per-session JSONL stage logs (handtracking/logs/).
//   Tools/Robot Teleop/Set Handtracking Folder…
//       Where the openvla-unity-sim2real handtracking/ dir lives (EditorPrefs).
//
// Cross-platform: Windows launches cmd.exe /k + taskkill; macOS/Linux open a
// Terminal window (a .command the OS runs) + pkill. GUI-launched editors don't
// inherit the login-shell PATH, so `uv` is resolved to an absolute path from
// the usual install locations (~/.local/bin/uv on macOS) rather than assumed
// to be on PATH.

using System;
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
        // Matches teleop_server.py invocations for pgrep/pkill on Unix.
        const string ServerMatch = "teleop_server.py";

        static bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor;

        static string Home =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        static string DefaultPath => IsWindows
            ? @"C:\Users\alire\Documents\Projects\robotics-unity\handtracking"
            : Path.Combine(Home, "Documents/Projects/openvla-unity-sim2real/handtracking");

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

            string uv = ResolveUv();
            if (uv == null)
            {
                EditorUtility.DisplayDialog(
                    "uv not found",
                    "Could not find the `uv` executable. Install it (https://docs.astral.sh/uv/) "
                    + "or, if it is installed somewhere unusual, add its folder to PATH.\n\n"
                    + "Looked in PATH and the usual spots (~/.local/bin, /opt/homebrew/bin, "
                    + "/usr/local/bin, ~/.cargo/bin).",
                    "OK");
                Debug.LogError("[VlaTeleop] `uv` not found — cannot start the teleop server.");
                return;
            }

            // --record-raw ARMS the raw-episode recorder (does not start it):
            // a double pinch in VR toggles recording; episodes land under
            // handtracking/episodes/ as teleop_raw_v1 for the fine-tune pipeline.
            string args = $"--robot {robot} --source quest-xr --record-raw episodes";
            string uvArgs = $"run --no-project --with numpy --with websockets "
                          + $"python teleop_server.py {args}";
            try
            {
                if (IsWindows) StartWindows(uv, uvArgs, dir, args);
                else StartUnix(uv, uvArgs, dir, args, robot);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VlaTeleop] Could not start the server ({e.Message}). "
                               + $"uv resolved to '{uv}', handtracking dir '{dir}'.");
            }
        }

        static void StartWindows(string uv, string uvArgs, string dir, string args)
        {
            // cmd /k keeps the console open (it IS the live heartbeat log).
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{uv}\" {uvArgs}\"",
                WorkingDirectory = dir,
                UseShellExecute = true,
            };
            var proc = Process.Start(psi);
            SessionState.SetInt(PidKey, proc.Id);
            Debug.Log($"[VlaTeleop] Teleop server console started (pid {proc.Id}): "
                      + $"teleop_server.py {args}  [{dir}]\n"
                      + "WS :8766 for GrootBridge, UDP :9905 for this app. "
                      + "Stop via Tools ▸ Robot Teleop ▸ Stop Teleop Server.");
        }

        static void StartUnix(string uv, string uvArgs, string dir, string args, string robot)
        {
            // macOS/Linux: write a .command script and open it in a Terminal
            // window — the window IS the live heartbeat log, matching the
            // Windows cmd /k UX. The editor's PATH lacks ~/.local/bin, so the
            // script prepends uv's own folder before calling it.
            string uvDir = Path.GetDirectoryName(uv);
            string script =
                "#!/bin/bash\n"
                + $"export PATH=\"{uvDir}:$PATH\"\n"
                + $"cd '{dir}' || exit 1\n"
                + $"echo '[VlaTeleop] {robot} teleop server — Ctrl-C or the Stop menu to quit.'\n"
                + $"'{uv}' {uvArgs}\n"
                + "code=$?\n"
                + "echo\n"
                + "echo \"[VlaTeleop] server exited (code $code) — press any key to close.\"\n"
                + "read -n 1\n";
            string scriptPath = Path.Combine(Path.GetTempPath(), "vla_teleop_server.command");
            File.WriteAllText(scriptPath, script);
            Run("/bin/chmod", $"+x \"{scriptPath}\"")?.WaitForExit(3000);

            if (Application.platform == RuntimePlatform.OSXEditor)
                Run("/usr/bin/open", $"-a Terminal \"{scriptPath}\"");
            else                                   // Linux best-effort
                Run("/usr/bin/xdg-open", $"\"{scriptPath}\"");

            Debug.Log($"[VlaTeleop] Teleop server launched in a Terminal window: "
                      + $"teleop_server.py {args}  [{dir}]\n"
                      + $"uv: {uv}\nWS :8766 for GrootBridge, UDP :9905 for this app. "
                      + "Stop via Tools ▸ Robot Teleop ▸ Stop Teleop Server (or Ctrl-C in the window).");
        }

        [MenuItem("Tools/Robot Teleop/Stop Teleop Server", priority = 43)]
        public static void StopServer()
        {
            if (IsWindows)
            {
                int pid = SessionState.GetInt(PidKey, 0);
                if (pid <= 0) { Debug.Log("[VlaTeleop] No teleop server tracked by this session."); return; }
                Run("taskkill", $"/PID {pid} /T /F", hidden: true)?.WaitForExit(5000);
                SessionState.EraseInt(PidKey);
                Debug.Log($"[VlaTeleop] Teleop server (pid {pid}) stopped.");
                return;
            }
            // Unix: the Terminal launch doesn't hand us the python pid, so match
            // by command line. Kills exactly the teleop_server.py process.
            var p = Run("/usr/bin/pkill", $"-f {ServerMatch}", hidden: true);
            p?.WaitForExit(5000);
            bool killed = p != null && p.ExitCode == 0;
            Debug.Log(killed
                ? "[VlaTeleop] Teleop server stopped."
                : "[VlaTeleop] No running teleop server found to stop.");
        }

        [MenuItem("Tools/Robot Teleop/Open Server Logs Folder", priority = 44)]
        public static void OpenLogs()
        {
            string logs = Path.Combine(HandtrackingPath, "logs");
            Directory.CreateDirectory(logs);
            EditorUtility.RevealInFinder(logs);
        }

        [MenuItem("Tools/Robot Teleop/Set Handtracking Folder…", priority = 45)]
        public static void SetPath()
        {
            string picked = EditorUtility.OpenFolderPanel(
                "openvla-unity-sim2real handtracking/ folder", HandtrackingPath, "");
            if (string.IsNullOrEmpty(picked)) return;
            EditorPrefs.SetString(PathPref, picked);
            Debug.Log($"[VlaTeleop] Handtracking folder set: {picked}");
        }

        /// <summary>Absolute path to the uv executable, or null if not found.
        /// GUI-launched editors don't inherit the login-shell PATH, so we probe
        /// the usual install locations rather than trusting PATH.</summary>
        static string ResolveUv()
        {
            if (IsWindows)
            {
                foreach (var c in new[]
                {
                    Path.Combine(Home, @".local\bin\uv.exe"),
                    Path.Combine(Home, @".cargo\bin\uv.exe"),
                    @"C:\Program Files\uv\uv.exe",
                })
                    if (File.Exists(c)) return c;
                return "uv.exe";     // fall back to PATH resolution by cmd
            }
            foreach (var c in new[]
            {
                Path.Combine(Home, ".local/bin/uv"),
                "/opt/homebrew/bin/uv",
                "/usr/local/bin/uv",
                Path.Combine(Home, ".cargo/bin/uv"),
            })
                if (File.Exists(c)) return c;

            // Last resort: ask a login shell where uv is (picks up custom setups).
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-lc \"command -v uv\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (!string.IsNullOrEmpty(outp) && File.Exists(outp)) return outp;
            }
            catch { /* fall through */ }
            return null;
        }

        static Process Run(string file, string args, bool hidden = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = hidden,
            };
            return Process.Start(psi);
        }

        [MenuItem("Tools/Robot Teleop/Stop Teleop Server", validate = true)]
        static bool ValidateStop() => IsWindows ? RunningPid() > 0 : true;

        static int RunningPid()
        {
            if (!IsWindows) return 0;      // Unix tracks by command match, not pid
            int pid = SessionState.GetInt(PidKey, 0);
            if (pid <= 0) return 0;
            try
            {
                var p = Process.GetProcessById(pid);
                return p.HasExited ? 0 : pid;
            }
            catch (Exception)
            {
                return 0;                  // exited / recycled pid
            }
        }
    }
}
