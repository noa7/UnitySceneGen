using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    // ── Request model ─────────────────────────────────────────────────────────
    public class ApiGenerateRequest
    {
        /// <summary>
        /// Absolute path to Unity.exe on the host machine.
        /// Defaults to the standard Unity Hub install path when omitted.
        /// </summary>
        [JsonProperty("unityExePath")]
        public string UnityExePath { get; set; }
            = AppSettings.DefaultUnityExePath;

        /// <summary>When true, deletes and recreates the Unity project from scratch.</summary>
        [JsonProperty("force")] public bool Force { get; set; }

        /// <summary>Full UnitySceneGen scene config (same schema as the .json file).</summary>
        [JsonProperty("config")] public JObject? Config { get; set; }
    }

    // ── Server ────────────────────────────────────────────────────────────────
    public sealed class ApiServer : IDisposable
    {
        public const int DefaultPort = 5782;

        private readonly int _port;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        // Unity can only run one headless instance at a time — enforce that here
        private readonly SemaphoreSlim _generateLock = new(1, 1);

        // ── Live status for GET /status polling ──────────────────────────────
        private readonly object _statusLock = new();
        private GenerationStatus _status = new();

        private void StatusLog(string line)
        {
            lock (_statusLock) { _status.Log.Add(line); }
            Console.WriteLine(line);
        }

        private void StatusStep(string step)
        {
            lock (_statusLock) { _status.Step = step; }
        }

        private GenerationStatus GetStatusSnapshot()
        {
            lock (_statusLock)
            {
                return new GenerationStatus
                {
                    Running = _status.Running,
                    Step = _status.Step,
                    Error = _status.Error,
                    Log = new List<string>(_status.Log),
                };
            }
        }


        public ApiServer(int port = DefaultPort)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[API] Listening  →  http://localhost:{_port}/");
            Console.WriteLine($"[API] Swagger UI →  http://localhost:{_port}/swagger");
            Console.WriteLine($"[API] OpenAPI    →  http://localhost:{_port}/openapi.json");
            Console.WriteLine($"[API] Generate   →  POST http://localhost:{_port}/generate");
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }

        public void Dispose() => Stop();

        // ── Accept loop ───────────────────────────────────────────────────────

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(ctx), ct);
            }
        }

        // ── Request dispatcher ────────────────────────────────────────────────

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            try
            {
                // Allow cross-origin requests so a browser-based Swagger UI can call this
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

                switch (req.HttpMethod, path)
                {
                    case ("GET", "") or
                         ("GET", "/"):
                        await WriteJsonAsync(res, RootInfo()); break;
                    case ("GET", "/openapi.json"): await WriteRawAsync(res, OpenApiSpec(), "application/json"); break;
                    case ("GET", "/swagger") or
                         ("GET", "/swagger/"):
                        await WriteRawAsync(res, SwaggerUiHtml(), "text/html"); break;
                    case ("GET", "/status"):
                        {
                            var snap = GetStatusSnapshot();
                            await WriteJsonAsync(res, snap);
                            break;
                        }
                    case ("POST", "/validate"): await HandleValidateAsync(req, res); break;
                    case ("POST", "/generate"): await HandleGenerateAsync(req, res); break;
                    case ("POST", "/build"): await HandleBuildAsync(req, res); break;
                    default:
                        res.StatusCode = 404;
                        await WriteJsonAsync(res, new { error = $"No route: {req.HttpMethod} {path}" });
                        break;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    res.StatusCode = 500;
                    await WriteJsonAsync(res, new { error = ex.Message });
                }
                catch { /* response already partially sent */ }
            }
        }

        // ── POST /validate ───────────────────────────────────────────────────────

        private async Task HandleValidateAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await sr.ReadToEndAsync();

            JObject? apiReq;
            try { apiReq = JObject.Parse(body); }
            catch (Exception ex)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = $"Could not parse request JSON: {ex.Message}" });
                return;
            }

            var configToken = apiReq?["config"];
            if (configToken == null)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = "config is required." });
                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"UnitySceneGen_val_{Guid.NewGuid():N}");
            var tempConfig = Path.Combine(Directory.CreateDirectory(tempDir).FullName, "SceneGenConfig.json");

            try
            {
                await File.WriteAllTextAsync(tempConfig,
                    JsonConvert.SerializeObject(configToken, Formatting.Indented));

                var result = ConfigValidator.Validate(tempConfig);

                res.StatusCode = result.Valid ? 200 : 422;
                await WriteJsonAsync(res, new
                {
                    valid = result.Valid,
                    errors = result.Errors,
                    warnings = result.Warnings,
                });
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // ── POST /generate ────────────────────────────────────────────────────

        private async Task HandleGenerateAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── 0. Concurrency guard ──────────────────────────────────────────
            // Unity's license system only allows one headless instance at a time.
            // Return 503 immediately if a generation is already in progress.
            if (!await _generateLock.WaitAsync(0))
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new
                {
                    error = "A generation is already in progress. Try again when it completes.",
                    status = "busy",
                });
                return;
            }

            try
            {
                lock (_statusLock) { _status = new GenerationStatus { Running = true, Step = "Starting…" }; }

                // ── 1. Parse body ─────────────────────────────────────────────
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                ApiGenerateRequest? apiReq;
                try { apiReq = JsonConvert.DeserializeObject<ApiGenerateRequest>(body); }
                catch (Exception ex)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = $"Could not parse request JSON: {ex.Message}" });
                    return;
                }

                // ── 2. Validate inputs ────────────────────────────────────────
                if (apiReq == null)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = "Request body could not be parsed." });
                    return;
                }
                if (apiReq.Config == null)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = "config is required." });
                    return;
                }

                // Resolve Unity path: use supplied value if non-empty, else default
                var unityExe = !string.IsNullOrWhiteSpace(apiReq.UnityExePath)
                    ? apiReq.UnityExePath
                    : AppSettings.DefaultUnityExePath;

                if (!File.Exists(unityExe))
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new
                    {
                        error = $"Unity executable not found: {unityExe}",
                        hint = "Supply 'unityExePath' in the request body or install Unity at the default path.",
                        defaultPath = AppSettings.DefaultUnityExePath,
                    });
                    return;
                }

                // ── 3. Write temp config, run generation ──────────────────────
                var sessionDir = Path.Combine(Path.GetTempPath(), $"UnitySceneGen_api_{Guid.NewGuid():N}");
                Directory.CreateDirectory(sessionDir);

                try
                {
                    var tempConfig = Path.Combine(sessionDir, "SceneGenConfig.json");
                    await File.WriteAllTextAsync(tempConfig,
                        JsonConvert.SerializeObject(apiReq.Config, Formatting.Indented));

                    var opts = new GenerationOptions
                    {
                        ConfigPath = tempConfig,
                        UnityExePath = unityExe,
                        OutputDir = sessionDir,
                        Force = apiReq.Force,
                    };

                    var logs = new List<string>();
                    var result = await Task.Run(() =>
                        GenerationEngine.Run(opts, line =>
                        {
                            logs.Add(line);
                            StatusLog(line);
                            if (line.Contains("Step 1/5")) StatusStep("Step 1/5 — Validating config");
                            else if (line.Contains("Step 2/5")) StatusStep("Step 2/5 — Setting up project");
                            else if (line.Contains("Step 3/5")) StatusStep("Step 3/5 — Unity Pass 1 (compile)");
                            else if (line.Contains("Step 4/5")) StatusStep("Step 4/5 — Unity Pass 2 (scene build)");
                            else if (line.Contains("Step 5/5")) StatusStep("Step 5/5 — Writing summary");
                        }, CancellationToken.None));

                    if (!result.Success)
                    {
                        lock (_statusLock) { _status.Error = result.Error; _status.Step = "Failed"; }
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new
                        {
                            error = result.Error,
                            warnings = result.Warnings,
                            log = logs,
                        });
                        return;
                    }

                    // ── 4. Zip and stream back ────────────────────────────────
                    var zipPath = Path.Combine(sessionDir, "project.zip");
                    var projectName = Path.GetFileName(result.ProjectPath);

                    ZipFile.CreateFromDirectory(result.ProjectPath, zipPath,
                        CompressionLevel.Optimal, includeBaseDirectory: true);

                    var zipBytes = await File.ReadAllBytesAsync(zipPath);

                    res.StatusCode = 200;
                    res.ContentType = "application/zip";
                    res.AddHeader("Content-Disposition",
                        $"attachment; filename=\"{projectName}.zip\"");
                    res.AddHeader("X-Warnings", string.Join("|", result.Warnings));
                    res.ContentLength64 = zipBytes.Length;
                    await res.OutputStream.WriteAsync(zipBytes);
                    res.OutputStream.Close(); // finalises the HTTP response — without this the client never receives the body
                }
                finally
                {
                    // Best-effort cleanup — on Windows Unity may hold file handles briefly
                    try { Directory.Delete(sessionDir, recursive: true); } catch { }
                }
            } // end semaphore try
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                _generateLock.Release();
            }
        }

        // ── POST /build ──────────────────────────────────────────────────────────
        // Accepts a ZIP of the Unity project source, builds WebGL, uploads to GCS,
        // returns { success, url, warnings[], log[] }. Streams via GET /status.

        private async Task HandleBuildAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (!await _generateLock.WaitAsync(0))
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new { error = "A job is already running.", status = "busy" });
                return;
            }

            try
            {
                lock (_statusLock) { _status = new GenerationStatus { Running = true, Step = "Build: receiving source…" }; }

                // ── 1. Read request body (JSON) ───────────────────────────────
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                JObject? apiReq;
                try { apiReq = JObject.Parse(body); }
                catch (Exception ex)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = $"Bad JSON: {ex.Message}" });
                    return;
                }

                // Expect: { "projectZipBase64": "...", "projectName": "...", "unityExePath": "..." }
                string? zipBase64 = apiReq?["projectZipBase64"]?.Value<string>();
                string? projectName = apiReq?["projectName"]?.Value<string>();
                string? unityExe = apiReq?["unityExePath"]?.Value<string>();
                string? gcsBucket = apiReq?["gcsBucket"]?.Value<string>() ?? "aqe-unity-builds";
                string? gcsKeyJson = apiReq?["gcsKeyJson"]?.Value<string>(); // base64 service account JSON
                bool development = apiReq?["development"]?.Value<bool>() ?? false;

                if (string.IsNullOrWhiteSpace(zipBase64)) { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectZipBase64 is required." }); return; }
                if (string.IsNullOrWhiteSpace(projectName)) { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectName is required." }); return; }

                unityExe = string.IsNullOrWhiteSpace(unityExe) ? AppSettings.DefaultUnityExePath : unityExe;
                if (!File.Exists(unityExe)) { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Unity not found: {unityExe}" }); return; }

                // ── 2. Unzip project to temp folder ───────────────────────────
                var sessionDir = Path.Combine(Path.GetTempPath(), $"UnityBuild_{Guid.NewGuid():N}");
                var projectPath = Path.Combine(sessionDir, projectName);
                StatusLog($"[Build] Session dir: {sessionDir}");

                try
                {
                    Directory.CreateDirectory(sessionDir);
                    var zipBytes = Convert.FromBase64String(zipBase64);
                    var tempZip = Path.Combine(sessionDir, "source.zip");
                    await File.WriteAllBytesAsync(tempZip, zipBytes);

                    StatusLog($"[Build] Extracting source ZIP ({zipBytes.Length / 1024:N0} KB)…");
                    StatusStep("Build: extracting source…");
                    System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, sessionDir, overwriteFiles: true);

                    if (!Directory.Exists(projectPath))
                    {
                        // ZIP may have been created without the project subfolder — try direct extract
                        projectPath = sessionDir;
                    }

                    StatusLog($"[Build] Project path: {projectPath}");

                    // ── 3. Inject WebGLBuilder.cs ─────────────────────────────
                    StatusStep("Build: injecting build script…");
                    var editorDir = Path.Combine(projectPath, "Assets", "Editor");
                    Directory.CreateDirectory(editorDir);

                    string buildOut = Path.Combine(projectPath, "Builds", "WebGL").Replace("\\", "/");
                    string devFlag = development ? "BuildOptions.Development" : "BuildOptions.None";

                    // Build the script using string.Replace to avoid interpolation escaping issues
                    string builderScript =
                        "using UnityEditor;\n" +
                        "using UnityEngine;\n" +
                        "using System.IO;\n" +
                        "using System.Collections.Generic;\n\n" +
                        "public class WebGLBuilder\n" +
                        "{\n" +
                        "    public static void Build()\n" +
                        "    {\n" +
                        "        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);\n" +
                        "        PlayerSettings.WebGL.memorySize = 1024;\n" +
                        "        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;\n" +
                        "        PlayerSettings.WebGL.dataCaching = true;\n" +
                        "        PlayerSettings.WebGL.decompressionFallback = true;\n\n" +
                        "        var scenes = new List<string>();\n" +
                        "        foreach (var f in Directory.GetFiles(\"Assets\", \"*.unity\", SearchOption.AllDirectories))\n" +
                        "            scenes.Add(f.Replace(\"\\\\\", \"/\"));\n\n" +
                        "        if (scenes.Count == 0) { Debug.LogError(\"WebGLBuilder: no .unity scenes found\"); return; }\n" +
                        "        foreach (var s in scenes) Debug.Log(\"[Build] Scene: \" + s);\n\n" +
                        $"        string outPath = \"{buildOut.Replace("\\", "/")}\";\n" +
                        $"        BuildOptions buildOpts = {devFlag};\n" +
                        "        var report = BuildPipeline.BuildPlayer(scenes.ToArray(), outPath, BuildTarget.WebGL, buildOpts);\n" +
                        "        Debug.Log(\"[Build] Result: \" + report.summary.result);\n" +
                        "        Debug.Log(\"[Build] Output: \" + outPath);\n" +
                        "        Debug.Log(\"[Build] Errors: \" + report.summary.totalErrors);\n" +
                        "    }\n" +
                        "}\n";
                    await File.WriteAllTextAsync(Path.Combine(editorDir, "WebGLBuilder.cs"), builderScript);
                    StatusLog("[Build] WebGLBuilder.cs injected.");

                    // ── 4. Run Unity build pass ───────────────────────────────
                    StatusStep("Build: Unity building WebGL…");
                    StatusLog("[Build] Starting Unity WebGL build…");

                    var buildLogFile = Path.Combine(sessionDir, "UnityBuild.log");
                    var unityArgs = $"-batchmode -quit -nographics -projectPath \"{projectPath}\" -executeMethod WebGLBuilder.Build -logFile \"{buildLogFile}\"";

                    StatusLog($"[Build] Unity: {unityExe}");
                    StatusLog($"[Build] Args : {unityArgs}");

                    var buildResult = await RunUnityBuildAsync(unityExe, unityArgs, buildLogFile,
                        TimeSpan.FromMinutes(20), CancellationToken.None);

                    if (!buildResult.ok)
                    {
                        lock (_statusLock) { _status.Step = "Build: FAILED"; _status.Error = buildResult.error; }
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new { success = false, error = buildResult.error, log = _status.Log });
                        return;
                    }

                    string buildOutAbs = Path.Combine(projectPath, "Builds", "WebGL");
                    if (!Directory.Exists(buildOutAbs))
                    {
                        var err = $"Build succeeded but output folder not found: {buildOutAbs}";
                        lock (_statusLock) { _status.Step = "Build: FAILED"; _status.Error = err; }
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new { success = false, error = err, log = _status.Log });
                        return;
                    }

                    StatusLog($"[Build] ✓ WebGL build complete: {buildOutAbs}");

                    // ── 5. Upload to GCS if key provided ─────────────────────
                    string publicUrl = "";
                    if (!string.IsNullOrWhiteSpace(gcsKeyJson) && !string.IsNullOrWhiteSpace(gcsBucket))
                    {
                        StatusStep("Build: uploading to GCS…");
                        StatusLog($"[Build] Uploading to gs://{gcsBucket}/{projectName}/");

                        var keyBytes = Convert.FromBase64String(gcsKeyJson);
                        var tempKey = Path.Combine(sessionDir, "svc.json");
                        await File.WriteAllBytesAsync(tempKey, keyBytes);

                        var gcsResult = await UploadBuildToGcsAsync(buildOutAbs, gcsBucket, projectName, tempKey);
                        publicUrl = gcsResult;
                        StatusLog($"[Build] ✓ Uploaded. URL: {publicUrl}");
                    }
                    else
                    {
                        StatusLog("[Build] No GCS key provided — skipping upload.");
                    }

                    StatusStep("Build: complete ✓");
                    lock (_statusLock) { _status.Running = false; }

                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new
                    {
                        success = true,
                        url = publicUrl,
                        warnings = new List<string>(),
                        log = _status.Log,
                    });
                }
                finally
                {
                    try { Directory.Delete(sessionDir, recursive: true); } catch { }
                }
            }
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                _generateLock.Release();
            }
        }

        // Runs Unity as a child process, tails the log file into StatusLog
        private async Task<(bool ok, string error)> RunUnityBuildAsync(
            string exe, string args, string logFile, TimeSpan timeout, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Unity process.");

            StatusLog($"[Build] Unity PID {proc.Id}");

            var tailCts = new CancellationTokenSource();
            long lastSize = 0;

            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                while (!tailCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(logFile))
                        {
                            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            if (fs.Length > lastSize)
                            {
                                fs.Seek(lastSize, SeekOrigin.Begin);
                                using var sr = new StreamReader(fs);
                                var text = sr.ReadToEnd();
                                lastSize = fs.Length;
                                foreach (var line in text.Split('
'))
                                {
                                    var t = line.Trim();
                                    if (t.Length > 0) StatusLog($"  [Unity] {t}");
                                }
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(800);
                }
            });

            var deadline = DateTime.UtcNow + timeout;
            while (!proc.HasExited)
            {
                if (DateTime.UtcNow > deadline)
                {
                    try { proc.Kill(true); } catch { }
                    tailCts.Cancel();
                    return (false, $"Unity build timed out after {timeout.TotalMinutes:F0} min.");
                }
                await Task.Delay(1000);
            }

            tailCts.Cancel();
            await Task.Delay(600);

            int exit = proc.ExitCode;
            StatusLog($"[Build] Unity exited: {exit}");

            if (File.Exists(logFile))
            {
                var log = File.ReadAllText(logFile);
                if (log.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
                    log.Contains("Error building Player", StringComparison.OrdinalIgnoreCase))
                    return (false, "Unity build failed — check log.");
            }

            return (exit == 0, exit == 0 ? "" : $"Unity exited with code {exit}");
        }

        // Upload WebGL build folder to GCS, returns public URL of index.html
        private async Task<string> UploadBuildToGcsAsync(
            string buildFolder, string bucket, string projectName, string keyFilePath)
        {
            // Use gsutil via process — avoids needing GCS SDK on the server
            // Falls back to a simple HTTP PUT loop if gsutil not available
            var files = Directory.EnumerateFiles(buildFolder, "*", SearchOption.AllDirectories).ToList();
            StatusLog($"[GCS] {files.Count} files to upload");

            // Try gsutil rsync
            var gsutil = "gsutil";
            var gsArgs = $"-m cp -r \"{buildFolder}\" \"gs://{bucket}/{projectName}/\"";

            var psi = new ProcessStartInfo(gsutil, gsArgs)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["GOOGLE_APPLICATION_CREDENTIALS"] = keyFilePath }
            };

            try
            {
                using var p = Process.Start(psi)!;
                p.OutputDataReceived += (_, e) => { if (e.Data != null) StatusLog($"  [gsutil] {e.Data}"); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) StatusLog($"  [gsutil] {e.Data}"); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                StatusLog($"[GCS] gsutil exit: {p.ExitCode}");
            }
            catch (Exception ex)
            {
                StatusLog($"[GCS] gsutil not available: {ex.Message}. Upload skipped.");
            }

            return $"https://storage.googleapis.com/{bucket}/{projectName}/index.html";
        }

        private object RootInfo() => new
        {
            service = "UnitySceneGen API",
            version = "1.0.0",
            port = _port,
            defaultUnityExePath = AppSettings.DefaultUnityExePath,
            endpoints = new[]
            {
                $"GET  http://localhost:{_port}/swagger       — Swagger UI (browser)",
                $"GET  http://localhost:{_port}/openapi.json  — OpenAPI 3.0 spec",
                $"POST http://localhost:{_port}/generate      — Generate project, returns .zip",
            },
        };

        // ── Swagger UI ────────────────────────────────────────────────────────

        private string SwaggerUiHtml() => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>UnitySceneGen API — Swagger UI</title>
  <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"/>
</head>
<body>
<div id="swagger-ui"></div>
<script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
<script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-standalone-preset.js"></script>
<script>
  SwaggerUIBundle({
    url:            "/openapi.json",
    dom_id:         "#swagger-ui",
    presets:        [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
    layout:         "StandaloneLayout",
    deepLinking:    true,
    tryItOutEnabled: true,
    requestInterceptor: (req) => {
      // Ensure the browser sends the request directly to the local server
      return req;
    }
  });
</script>
</body>
</html>
""";

        private static async Task WriteJsonAsync(HttpListenerResponse res, object obj)
            => await WriteRawAsync(res, JsonConvert.SerializeObject(obj, Formatting.Indented), "application/json");

        private static async Task WriteRawAsync(HttpListenerResponse res, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = $"{contentType}; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.OutputStream.Close();
        }

        // ── OpenAPI 3.0 spec ──────────────────────────────────────────────────

        private string OpenApiSpec() => $$"""
{
  "openapi": "3.0.3",
  "info": {
    "title": "UnitySceneGen API",
    "description": "Generate a complete Unity project from a JSON scene config and receive it as a ZIP archive.\n\n**Default Unity path:** `{{AppSettings.DefaultUnityExePath.Replace("\\", "\\\\")}}`\n\nOmit `unityExePath` from the request body to use this default.\n\n**Note:** `POST /generate` runs the full Unity headless pipeline (Pass 1 + Pass 2) and may take 5–20 minutes. Keep client timeouts high accordingly.",
    "version": "1.0.0"
  },
  "servers": [{ "url": "/", "description": "UnitySceneGen server" }],
  "paths": {
    "/swagger": {
      "get": {
        "summary": "Swagger UI",
        "description": "Interactive browser-based API explorer.",
        "operationId": "swaggerUi",
        "responses": {
          "200": { "description": "HTML page", "content": { "text/html": {} } }
        }
      }
    },
    "/generate": {
      "post": {
        "summary": "Generate a Unity project",
        "description": "Runs the full two-pass Unity generation pipeline and returns the generated project folder as a ZIP file.",
        "operationId": "generateProject",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": { "$ref": "#/components/schemas/GenerateRequest" },
              "example": {
                "config": {
                  "project":  { "name": "MyProject", "unityVersion": "2022.3.20f1" },
                  "settings": { "tags": ["Player"], "layers": ["Gameplay"] },
                  "scenes":   [{ "name": "Main", "path": "Assets/Scenes/Main.unity", "roots": ["go.root"] }],
                  "gameObjects": [
                    { "id": "go.root", "name": "Root", "children": ["go.cam"] },
                    { "id": "go.cam",  "name": "Main Camera",
                      "components": [
                        { "type": "UnityEngine.Camera" },
                        { "type": "UnityEngine.AudioListener" }
                      ]
                    }
                  ]
                }
              }
            }
          }
        },
        "responses": {
          "200": {
            "description": "Project generated successfully. Body is a ZIP archive of the Unity project folder.",
            "headers": {
              "Content-Disposition": {
                "schema": { "type": "string" },
                "description": "attachment; filename=\\\"<projectName>.zip\\\""
              },
              "X-Warnings": {
                "schema": { "type": "string" },
                "description": "Pipe-separated builder warnings. Empty string when there are none."
              }
            },
            "content": {
              "application/zip": {
                "schema": { "type": "string", "format": "binary" }
              }
            }
          },
          "400": {
            "description": "Bad request — missing required fields or Unity.exe not found.",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ErrorResponse" } } }
          },
          "422": {
            "description": "Config parsed and validated but the Unity generation pipeline failed.",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ErrorResponse" } } }
          },
          "500": {
            "description": "Unexpected server error.",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ErrorResponse" } } }
          },
          "503": {
            "description": "Another generation is already running. Unity only supports one headless instance at a time. Retry when the current job completes.",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ErrorResponse" } } }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "GenerateRequest": {
        "type": "object",
        "required": ["config"],
        "properties": {
          "unityExePath": {
            "type": "string",
            "description": "Absolute path to Unity.exe on the server machine. Omit to use the default path.",
            "default": "{{AppSettings.DefaultUnityExePath.Replace("\\", "\\\\")}}"
          },
          "force": {
            "type": "boolean",
            "default": false,
            "description": "When true, deletes and recreates the Unity project from scratch before generating."
          },
          "config": {
            "type": "object",
            "description": "Full UnitySceneGen scene config. Same JSON schema as the .json config file — see README for full reference."
          }
        }
      },
      "ErrorResponse": {
        "type": "object",
        "properties": {
          "error":       { "type": "string", "description": "Human-readable error message." },
          "hint":        { "type": "string", "description": "Actionable suggestion (present on some 400 errors)." },
          "defaultPath": { "type": "string", "description": "The default Unity path the server tried (present when exe not found)." },
          "warnings":    { "type": "array",  "items": { "type": "string" }, "description": "Non-fatal builder warnings." },
          "log":         { "type": "array",  "items": { "type": "string" }, "description": "Full generation log lines (only present on 422)." }
        }
      }
    }
  }
}
""";
    }

    public class GenerationStatus
    {
        [JsonProperty("running")] public bool Running { get; set; }
        [JsonProperty("step")] public string Step { get; set; } = "Idle";
        [JsonProperty("error")] public string Error { get; set; } = "";
        [JsonProperty("log")] public List<string> Log { get; set; } = new();
    }
}