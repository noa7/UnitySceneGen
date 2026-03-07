using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class UnityLauncher
    {
        // ── Timeouts ──────────────────────────────────────────────────
        private static readonly TimeSpan Pass1Timeout = TimeSpan.FromMinutes(12);
        private static readonly TimeSpan Pass2Timeout = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan HangDetectWindow = TimeSpan.FromSeconds(120);

        // ── Sentinel filenames ────────────────────────────────────────
        private const string ResultFile = "SceneGenResult.json";
        private const string RunningFile = "SceneGenRunning.json";

        // ─────────────────────────────────────────────────────────────
        // Pass 1 — Import packages + compile scripts (no executeMethod)
        // ─────────────────────────────────────────────────────────────
        public static async Task<(bool ok, string error)> Pass1ImportAsync(
            string unityExe, string projectPath,
            Action<string> log, CancellationToken ct)
        {
            log("[Unity Pass 1] Starting — package import & script compilation…");
            var logFile = Path.Combine(projectPath, "SceneGenUnity_Pass1.log");
            var args = BuildArgs(projectPath, logFile, executeMethod: null);

            // Unity 6 exits with code 1 even on successful compilation.
            // Pass 1 success = absence of failure patterns, not presence of success string.
            return await RunUnityAsync(unityExe, args, logFile,
                Pass1Timeout, log, ct,
                successPattern: null,
                failurePatterns: new[] { "compilation errors", "Error building Player",
                                         "Failed to compile", "Scripts have compile errors",
                                         "error CS" });
        }

        // ─────────────────────────────────────────────────────────────
        // Pass 2 — Run scene builder
        // ─────────────────────────────────────────────────────────────
        public static async Task<BuilderResult> Pass2BuildAsync(
            string unityExe, string projectPath,
            Action<string> log, CancellationToken ct)
        {
            log("[Unity Pass 2] Starting — scene generation…");

            TryDelete(Path.Combine(projectPath, ResultFile));
            TryDelete(Path.Combine(projectPath, RunningFile));

            var logFile = Path.Combine(projectPath, "SceneGenUnity_Pass2.log");
            var args = BuildArgs(projectPath, logFile,
                                    executeMethod: "UnitySceneGen.Builder.Run");

            var (ok, error) = await RunUnityAsync(unityExe, args, logFile,
                Pass2Timeout, log, ct,
                successPattern: "Exiting batchmode",
                failurePatterns: new[] { "compilation errors", "Scripts have compile errors" });

            // Read sentinel regardless of exit code — Unity's is unreliable
            var resultPath = Path.Combine(projectPath, ResultFile);

            if (!File.Exists(resultPath))
            {
                return ok
                    ? Fail($"Unity exited cleanly but Builder never wrote its result file. " +
                           $"Check log: {logFile}")
                    : Fail($"Unity failed before Builder.Run executed. {error} " +
                           $"Check log: {logFile}");
            }

            try
            {
                var result = JsonConvert.DeserializeObject<BuilderResult>(
                                 File.ReadAllText(resultPath));
                return result ?? Fail("SceneGenResult.json was empty or malformed.");
            }
            catch (Exception ex)
            {
                return Fail($"Could not parse SceneGenResult.json: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Core process runner
        // ─────────────────────────────────────────────────────────────
        private static async Task<(bool ok, string error)> RunUnityAsync(
            string exe, string args, string logFile,
            TimeSpan timeout, Action<string> log, CancellationToken ct,
            string? successPattern, string[]? failurePatterns)
        {
            log($"[Unity] Exe : \"{exe}\"");
            log($"[Unity] Args: {args}");
            log($"[Unity] Log : {logFile}");

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Unity process.");

            log($"[Unity] PID {proc.Id} started.");

            // ── Log tail task ─────────────────────────────────────────
            var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastActivity = DateTime.UtcNow;
            long lastSize = 0;

            _ = Task.Run(async () =>
            {
                await Task.Delay(2000, tailCts.Token).ContinueWith(_ => { });

                while (!tailCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(logFile))
                        {
                            using var fs = new FileStream(logFile,
                                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            if (fs.Length > lastSize)
                            {
                                fs.Seek(lastSize, SeekOrigin.Begin);
                                using var sr = new StreamReader(fs);
                                var newText = sr.ReadToEnd();
                                lastSize = fs.Length;
                                lastActivity = DateTime.UtcNow;

                                foreach (var line in newText.Split('\n'))
                                {
                                    var t = line.Trim();
                                    if (t.Length > 0) log($"  [Unity] {t}");
                                }
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(500, tailCts.Token).ContinueWith(_ => { });
                }
            }, tailCts.Token);

            // ── Wait loop with timeout + hang detection ───────────────
            var deadline = DateTime.UtcNow + timeout;
            bool timedOut = false;
            bool hangKill = false;

            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    log("[Unity] Cancellation requested — killing.");
                    KillSafe(proc);
                    tailCts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                if (DateTime.UtcNow > deadline)
                {
                    timedOut = true;
                    log($"[Unity] TIMEOUT after {timeout.TotalMinutes:F0} min — killing.");
                    KillSafe(proc);
                    break;
                }

                if (DateTime.UtcNow - lastActivity > HangDetectWindow)
                {
                    hangKill = true;
                    log($"[Unity] No log output for {HangDetectWindow.TotalSeconds:F0}s " +
                        "(possible license error or silent hang) — killing.");
                    KillSafe(proc);
                    break;
                }

                await Task.Delay(1000);
            }

            tailCts.Cancel();
            await Task.Delay(600); // let tail flush last lines

            int exitCode = (timedOut || hangKill) ? -1 : proc.ExitCode;
            log($"[Unity] Process ended — exit code {exitCode}");

            if (timedOut) return (false, $"Unity timed out after {timeout.TotalMinutes:F0} min.");
            if (hangKill) return (false, "Unity stopped producing output (license/hang).");

            // Scan log for fatal patterns
            if (File.Exists(logFile))
            {
                var content = ReadSafe(logFile);
                if (failurePatterns != null)
                    foreach (var pat in failurePatterns)
                        if (content.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                            return (false, $"Unity log contains failure: '{pat}'");

                if (successPattern != null
                    && exitCode != 0
                    && content.IndexOf(successPattern, StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, $"Unity exited {exitCode} without success pattern.");
            }

            // If no success pattern is required and no failure patterns were found,
            // treat the run as successful even if Unity exited with a non-zero code.
            // Unity 6 regularly exits with code 1 on success in batchmode.
            bool hasSuccessPattern = successPattern == null
                || (File.Exists(logFile) && ReadSafe(logFile)
                        .IndexOf(successPattern, StringComparison.OrdinalIgnoreCase) >= 0);
            bool clean = (exitCode == 0 || exitCode == 1) && hasSuccessPattern;
            return (clean, clean ? "" : $"Unity exited with code {exitCode}");
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static string BuildArgs(string projectPath, string logFile, string? executeMethod)
        {
            var sb = new StringBuilder();
            sb.Append("-batchmode -quit -nographics");
            sb.Append($" -projectPath \"{projectPath}\"");
            sb.Append($" -logFile \"{logFile}\"");
            if (!string.IsNullOrEmpty(executeMethod))
                sb.Append($" -executeMethod {executeMethod}");
            return sb.ToString();
        }

        private static void KillSafe(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string ReadSafe(string path)
        {
            try
            {
                using var fs = new FileStream(path,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static BuilderResult Fail(string error) =>
            new BuilderResult { Success = false, Error = error };
    }
}