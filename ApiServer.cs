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
    // ─────────────────────────────────────────────────────────────────────────
    // API server
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class ApiServer : IDisposable
    {
        public const int DefaultPort = 5782;

        private readonly int _port;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _generateLock = new(1, 1);

        // ── Live status (polled by GET /status) ──────────────────────────────
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
                return new GenerationStatus
                {
                    Running = _status.Running,
                    Step = _status.Step,
                    Error = _status.Error,
                    Log = new List<string>(_status.Log),
                };
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public ApiServer(int port = DefaultPort)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"[API] ─────────────────────────────────────────");
            Console.WriteLine($"[API] UnitySceneGen API  →  http://localhost:{_port}/");
            Console.WriteLine($"[API]");
            Console.WriteLine($"[API] Recommended workflow:");
            Console.WriteLine($"[API]   1. GET  /schema    — fetch component + template catalog");
            Console.WriteLine($"[API]   2. Build scene.zip (scene.json + scripts/ folder)");
            Console.WriteLine($"[API]   3. POST /validate  — validate zip, no Unity needed (< 1 s)");
            Console.WriteLine($"[API]   4. POST /generate  — run full pipeline, returns Unity project .zip");
            Console.WriteLine($"[API]   5. GET  /status    — poll progress while /generate runs");
            Console.WriteLine($"[API]");
            Console.WriteLine($"[API] Swagger UI  →  http://localhost:{_port}/swagger");
            Console.WriteLine($"[API] OpenAPI     →  http://localhost:{_port}/openapi.json");
            Console.WriteLine($"[API] ─────────────────────────────────────────");
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
                    case ("GET", "") or ("GET", "/"):
                        await WriteJsonAsync(res, RootInfo()); break;

                    case ("GET", "/schema"):
                        await WriteJsonAsync(res, ComponentSchema.Build()); break;

                    case ("GET", "/status"):
                        await WriteJsonAsync(res, GetStatusSnapshot()); break;

                    case ("GET", "/swagger") or ("GET", "/swagger/"):
                        await WriteRawAsync(res, SwaggerUiHtml(), "text/html"); break;

                    case ("GET", "/openapi.json"):
                        await WriteRawAsync(res, OpenApiSpec(), "application/json"); break;

                    case ("POST", "/validate"):
                        await HandleValidateAsync(req, res); break;

                    case ("POST", "/generate"):
                        await HandleGenerateAsync(req, res); break;

                    case ("POST", "/build"):
                        await HandleBuildAsync(req, res); break;

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
                    await WriteJsonAsync(res, new { error = $"Internal server error: {ex.Message}" });
                }
                catch { }
            }
        }

        // ── POST /validate ────────────────────────────────────────────────────
        //
        // Input:  { "sceneZipBase64": "<base64>" }
        // Output: { "valid": bool, "errors": [...], "warnings": [...] }
        //
        // Extracts the zip, resolves templates, runs the validator.
        // Does NOT launch Unity — fast (< 1 s).

        private async Task HandleValidateAsync(
            HttpListenerRequest req, HttpListenerResponse res)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await sr.ReadToEndAsync();

            string? zipBase64;
            try
            {
                var obj = JObject.Parse(body);
                zipBase64 = obj["sceneZipBase64"]?.Value<string>();
            }
            catch (Exception ex)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = $"Could not parse request JSON: {ex.Message}" });
                return;
            }

            if (string.IsNullOrWhiteSpace(zipBase64))
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new
                {
                    error = "sceneZipBase64 is required.",
                    hint = "Base64-encode your scene.zip and send it in the sceneZipBase64 field.",
                });
                return;
            }

            byte[] zipBytes;
            try { zipBytes = Convert.FromBase64String(zipBase64); }
            catch
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = "sceneZipBase64 is not valid base64." });
                return;
            }

            var result = GenerationEngine.ValidateZip(zipBytes);

            res.StatusCode = result.Valid ? 200 : 422;
            await WriteJsonAsync(res, new
            {
                valid = result.Valid,
                errors = result.Errors,
                warnings = result.Warnings,
            });
        }

        // ── POST /generate ────────────────────────────────────────────────────
        //
        // Input:  {
        //   "sceneZipBase64": "<base64 of scene.zip>",
        //   "unityExePath":   "C:\\...\\Unity.exe",   (optional, uses default)
        //   "outputDir":      "D:\\UnityProjects",     (optional, uses temp)
        //   "force":          false
        // }
        // Output: binary ZIP of the generated Unity project
        //         Content-Disposition: attachment; filename="<ProjectName>.zip"
        //
        // Holds the connection open for the full Unity pipeline (5–20 min).
        // Set client timeout >= 30 minutes.
        // Returns 503 immediately if another job is running.

        private async Task HandleGenerateAsync(
            HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Concurrency guard ─────────────────────────────────────────────
            if (!await _generateLock.WaitAsync(0))
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new
                {
                    error = "A generation is already in progress. Try again when it completes.",
                    status = "busy",
                    hint = "Poll GET /status until 'running' is false, then retry.",
                });
                return;
            }

            try
            {
                lock (_statusLock)
                    _status = new GenerationStatus { Running = true, Step = "Starting…" };

                // ── Parse request ─────────────────────────────────────────────
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                string? zipBase64, unityExeArg, outputDirArg;
                bool force;
                try
                {
                    var obj = JObject.Parse(body);
                    zipBase64 = obj["sceneZipBase64"]?.Value<string>();
                    unityExeArg = obj["unityExePath"]?.Value<string>();
                    outputDirArg = obj["outputDir"]?.Value<string>();
                    force = obj["force"]?.Value<bool>() ?? false;
                }
                catch (Exception ex)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = $"Could not parse request JSON: {ex.Message}" });
                    return;
                }

                // ── Validate inputs ───────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(zipBase64))
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new
                    {
                        error = "sceneZipBase64 is required.",
                        hint = "Base64-encode your scene.zip and pass it in sceneZipBase64.",
                        zipFormat = new
                        {
                            structure = new[]
                            {
                                "scene.zip",
                                "├── scene.json       ← required",
                                "└── scripts/         ← optional",
                                "    ├── MyScript.cs",
                                "    └── AnotherScript.cs",
                            },
                        },
                    });
                    return;
                }

                byte[] zipBytes;
                try { zipBytes = Convert.FromBase64String(zipBase64); }
                catch
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = "sceneZipBase64 is not valid base64." });
                    return;
                }

                var unityExe = !string.IsNullOrWhiteSpace(unityExeArg)
                    ? unityExeArg
                    : AppSettings.DefaultUnityExePath;

                if (!File.Exists(unityExe))
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new
                    {
                        error = $"Unity executable not found: {unityExe}",
                        hint = "Supply 'unityExePath' in the request body, or install Unity at the default path.",
                        defaultPath = AppSettings.DefaultUnityExePath,
                    });
                    return;
                }

                var outputDir = !string.IsNullOrWhiteSpace(outputDirArg)
                    ? outputDirArg
                    : Path.Combine(Path.GetTempPath(), $"UnitySceneGen_out_{Guid.NewGuid():N}");

                Directory.CreateDirectory(outputDir);

                // ── Run pipeline ──────────────────────────────────────────────
                var logs = new List<string>();
                void Log(string line)
                {
                    logs.Add(line);
                    StatusLog(line);
                    if (line.Contains("Step 1/6")) StatusStep("1/6 — Loading zip");
                    else if (line.Contains("Step 2/6")) StatusStep("2/6 — Resolving templates");
                    else if (line.Contains("Step 3/6")) StatusStep("3/6 — Validating config");
                    else if (line.Contains("Step 4/6")) StatusStep("4/6 — Setting up project");
                    else if (line.Contains("Step 5/6")) StatusStep("5/6 — Unity Pass 1 (compile)");
                    else if (line.Contains("Step 6/6")) StatusStep("6/6 — Unity Pass 2 (scene build)");
                }

                var result = await Task.Run(() =>
                    GenerationEngine.Run(
                        zipBytes, unityExe, outputDir, force,
                        Log, CancellationToken.None));

                if (!result.Success)
                {
                    lock (_statusLock)
                    {
                        _status.Error = result.Error;
                        _status.Step = "Failed";
                    }
                    res.StatusCode = 422;
                    await WriteJsonAsync(res, new
                    {
                        error = result.Error,
                        warnings = result.Warnings,
                        log = logs,
                    });
                    return;
                }

                // ── Zip and stream back ───────────────────────────────────────
                StatusStep("Done — zipping result…");

                var projectName = Path.GetFileName(result.ProjectPath);
                var zipOutPath = Path.Combine(outputDir, $"{projectName}_output.zip");

                ZipFile.CreateFromDirectory(
                    result.ProjectPath, zipOutPath,
                    CompressionLevel.Optimal, includeBaseDirectory: true);

                var zipOut = await File.ReadAllBytesAsync(zipOutPath);

                res.StatusCode = 200;
                res.ContentType = "application/zip";
                res.AddHeader("Content-Disposition",
                    $"attachment; filename=\"{projectName}.zip\"");
                res.AddHeader("X-Warnings", string.Join("|", result.Warnings));
                res.ContentLength64 = zipOut.Length;
                await res.OutputStream.WriteAsync(zipOut);
                res.OutputStream.Close();

                StatusLog($"[API] Done. Sent {zipOut.Length / 1024:N0} KB.");
            }
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                _generateLock.Release();
            }
        }

        // ── POST /build ───────────────────────────────────────────────────────
        //
        // WebGL build from a previously generated project zip.
        // Input:  { "projectZipBase64": "...", "projectName": "...",
        //           "unityExePath": "...", "gcsBucket": "...", "development": false }
        // Output: { "success": bool, "url": "...", "log": [...], "warnings": [...] }

        private async Task HandleBuildAsync(
            HttpListenerRequest req, HttpListenerResponse res)
        {
            if (!await _generateLock.WaitAsync(0))
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new { error = "A job is already running.", status = "busy" });
                return;
            }

            try
            {
                lock (_statusLock)
                    _status = new GenerationStatus { Running = true, Step = "Build: receiving source…" };

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

                string? zipBase64 = apiReq?["projectZipBase64"]?.Value<string>();
                string? projectName = apiReq?["projectName"]?.Value<string>();
                string? unityExe = apiReq?["unityExePath"]?.Value<string>();
                string? gcsBucket = apiReq?["gcsBucket"]?.Value<string>() ?? "aqe-unity-builds";
                string? gcsKeyJson = apiReq?["gcsKeyJson"]?.Value<string>();
                bool development = apiReq?["development"]?.Value<bool>() ?? false;

                if (string.IsNullOrWhiteSpace(zipBase64))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectZipBase64 is required." }); return; }
                if (string.IsNullOrWhiteSpace(projectName))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectName is required." }); return; }

                unityExe = string.IsNullOrWhiteSpace(unityExe) ? AppSettings.DefaultUnityExePath : unityExe;
                if (!File.Exists(unityExe))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Unity not found: {unityExe}" }); return; }

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
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new
                        {
                            error = $"Expected project folder '{projectName}' not found in zip. " +
                                    $"Make sure the zip was created with includeBaseDirectory=true.",
                        });
                        return;
                    }

                    // Write GCS key to temp file if supplied
                    string? keyFilePath = null;
                    if (!string.IsNullOrWhiteSpace(gcsKeyJson))
                    {
                        keyFilePath = Path.Combine(sessionDir, "gcs_key.json");
                        await File.WriteAllTextAsync(keyFilePath,
                            Encoding.UTF8.GetString(Convert.FromBase64String(gcsKeyJson)));
                    }

                    // WebGL build — Pass 1 compiles, Pass 2 runs the builder with WebGL target
                    StatusStep("Build: Unity Pass 1 — compile…");
                    var pass1 = await UnityLauncher.Pass1ImportAsync(
                        unityExe, projectPath, StatusLog, CancellationToken.None);

                    if (!pass1.ok)
                    {
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new { error = $"Pass 1 failed: {pass1.error}", log = _status.Log });
                        return;
                    }

                    StatusStep("Build: Unity Pass 2 — WebGL build…");
                    var pass2 = await UnityLauncher.Pass2BuildAsync(
                        unityExe, projectPath, StatusLog, CancellationToken.None);

                    if (!pass2.Success)
                    {
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new { error = $"Pass 2 failed: {pass2.Error}", log = _status.Log });
                        return;
                    }

                    // Upload
                    string? url = null;
                    if (!string.IsNullOrWhiteSpace(gcsBucket) && keyFilePath != null)
                    {
                        StatusStep("Build: uploading to GCS…");
                        url = await UploadBuildToGcsAsync(
                            Path.Combine(projectPath, "Build/WebGL"),
                            gcsBucket, projectName!, keyFilePath);
                    }

                    await WriteJsonAsync(res, new
                    {
                        success = true,
                        url,
                        buildPath = projectPath,
                        log = _status.Log,
                        warnings = new string[0],
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

        // ── GCS upload ────────────────────────────────────────────────────────

        private async Task<string> UploadBuildToGcsAsync(
            string buildFolder, string bucket, string projectName, string keyFilePath)
        {
            var files = Directory.EnumerateFiles(buildFolder, "*", SearchOption.AllDirectories).ToList();
            StatusLog($"[GCS] {files.Count} files to upload");

            var psi = new ProcessStartInfo("gsutil",
                $"-m cp -r \"{buildFolder}\" \"gs://{bucket}/{projectName}/\"")
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
                await Task.Run(() => p.WaitForExit());
                StatusLog($"[GCS] gsutil exit: {p.ExitCode}");
            }
            catch (Exception ex)
            {
                StatusLog($"[GCS] gsutil not available: {ex.Message}. Upload skipped.");
            }

            return $"https://storage.googleapis.com/{bucket}/{projectName}/index.html";
        }

        // ── Root info ─────────────────────────────────────────────────────────

        private object RootInfo() => new
        {
            service = "UnitySceneGen API",
            version = "2.0.0",
            port = _port,
            defaultUnityExePath = AppSettings.DefaultUnityExePath,
            recommendedWorkflow = new[]
            {
                $"1. GET  http://localhost:{_port}/schema    — fetch component + template catalog",
                $"2. Build your scene.zip  (scene.json + optional scripts/ folder)",
                $"3. POST http://localhost:{_port}/validate  — validate zip, no Unity needed (< 1 s)",
                $"4. POST http://localhost:{_port}/generate  — run full pipeline, returns Unity project .zip",
                $"5. GET  http://localhost:{_port}/status    — poll progress while /generate runs",
            },
            zipFormat = new
            {
                description = "scene.zip must contain scene.json at its root plus an optional scripts/ folder.",
                structure = new[]
                {
                    "scene.zip",
                    "├── scene.json       ← required: project + settings + scenes + gameObjects",
                    "└── scripts/         ← optional: .cs files copied as-is into Assets/Scripts/",
                    "    ├── MyScript.cs",
                    "    └── AnotherScript.cs",
                },
            },
            endpoints = new[]
            {
                $"GET  /schema        — component + template catalog (fetch before generating)",
                $"GET  /status        — live job status (safe to poll)",
                $"GET  /swagger       — Swagger UI",
                $"GET  /openapi.json  — OpenAPI 3.0 spec",
                $"POST /validate      — validate scene.zip without Unity (< 1 s)",
                $"POST /generate      — generate Unity project from scene.zip, returns .zip",
                $"POST /build         — WebGL build from generated project .zip",
            },
        };

        // ── Swagger UI ────────────────────────────────────────────────────────

        private string SwaggerUiHtml() => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <title>UnitySceneGen API</title>
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/5.11.0/swagger-ui.min.css" />
</head>
<body>
<div id="swagger-ui"></div>
<script src="https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/5.11.0/swagger-ui-bundle.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/5.11.0/swagger-ui-standalone-preset.min.js"></script>
<script>
  SwaggerUIBundle({
    url:            "/openapi.json",
    dom_id:         "#swagger-ui",
    presets:        [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
    layout:         "StandaloneLayout",
    deepLinking:    true,
    tryItOutEnabled: true,
  });
</script>
</body>
</html>
""";

        // ── OpenAPI spec ──────────────────────────────────────────────────────

        private string OpenApiSpec() => $$"""
{
  "openapi": "3.0.3",
  "info": {
    "title": "UnitySceneGen API",
    "version": "2.0.0",
    "description": "Generate a complete Unity project from a zipped scene config.\n\n**Input:** `scene.zip` containing `scene.json` + optional `scripts/` folder.\n**Output:** Unity project `.zip`.\n\n**Workflow:**\n1. `GET /schema` — fetch component + template catalog\n2. Build `scene.zip`\n3. `POST /validate` — fix errors cheaply (< 1 s)\n4. `POST /generate` — run full pipeline (5–20 min)\n5. `GET /status` — poll progress\n\n**Default Unity path:** `{{AppSettings.DefaultUnityExePath.Replace("\\", "\\\\")}}`"
  },
  "servers": [{ "url": "http://localhost:{{_port}}" }],
  "paths": {
    "/schema": {
      "get": {
        "summary": "Component + template catalog",
        "description": "Returns every supported Unity component type, all props with types and valid values, all built-in templates with their templateProps, prop format rules (Color, Vector, ref), and the ID convention. Fetch this once per session before generating configs.",
        "operationId": "getSchema",
        "responses": { "200": { "description": "Schema object" } }
      }
    },
    "/status": {
      "get": {
        "summary": "Live job status",
        "description": "Returns the current generation job status. Safe to poll at any frequency.",
        "operationId": "getStatus",
        "responses": {
          "200": {
            "description": "Status snapshot",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/StatusResponse" } } }
          }
        }
      }
    },
    "/validate": {
      "post": {
        "summary": "Validate scene.zip without running Unity",
        "description": "Extracts the zip, resolves templates, and runs all validation checks. Returns in under 1 second. Use this before calling /generate to catch errors cheaply.",
        "operationId": "validate",
        "requestBody": {
          "required": true,
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ZipRequest" } } }
        },
        "responses": {
          "200": { "description": "Valid", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ValidationResponse" } } } },
          "422": { "description": "Invalid — errors in response body", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ValidationResponse" } } } },
          "400": { "description": "Bad request (missing or malformed field)" }
        }
      }
    },
    "/generate": {
      "post": {
        "summary": "Generate Unity project from scene.zip",
        "description": "Runs the full pipeline: extract zip → resolve templates → validate → scaffold project → Unity Pass 1 (compile) → Unity Pass 2 (scene build) → return project as .zip.\n\n**Timing:** 5–20 minutes. Set client timeout >= 30 minutes.\n\n**Concurrency:** returns 503 immediately if another job is running. Poll `/status` until `running` is false, then retry.",
        "operationId": "generate",
        "requestBody": {
          "required": true,
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/GenerateRequest" } } }
        },
        "responses": {
          "200": { "description": "Unity project zip", "content": { "application/zip": {} } },
          "400": { "description": "Bad request" },
          "422": { "description": "Generation failed", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ErrorResponse" } } } },
          "503": { "description": "Another job is running" }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "ZipRequest": {
        "type": "object",
        "required": ["sceneZipBase64"],
        "properties": {
          "sceneZipBase64": {
            "type": "string",
            "description": "Base64-encoded scene.zip. The zip must contain scene.json at its root plus an optional scripts/ folder with .cs files."
          }
        }
      },
      "GenerateRequest": {
        "type": "object",
        "required": ["sceneZipBase64"],
        "properties": {
          "sceneZipBase64": {
            "type": "string",
            "description": "Base64-encoded scene.zip."
          },
          "unityExePath": {
            "type": "string",
            "description": "Absolute path to Unity.exe on the server machine. Omit to use the server default.",
            "default": "{{AppSettings.DefaultUnityExePath.Replace("\\", "\\\\")}}"
          },
          "outputDir": {
            "type": "string",
            "description": "Directory on the server to write the generated project. Defaults to a temp directory."
          },
          "force": {
            "type": "boolean",
            "default": false,
            "description": "When true, deletes and recreates the Unity project folder from scratch."
          }
        }
      },
      "ValidationResponse": {
        "type": "object",
        "properties": {
          "valid":    { "type": "boolean" },
          "errors":   { "type": "array", "items": { "type": "string" } },
          "warnings": { "type": "array", "items": { "type": "string" } }
        }
      },
      "StatusResponse": {
        "type": "object",
        "properties": {
          "running": { "type": "boolean" },
          "step":    { "type": "string" },
          "error":   { "type": "string" },
          "log":     { "type": "array", "items": { "type": "string" } }
        }
      },
      "ErrorResponse": {
        "type": "object",
        "properties": {
          "error":    { "type": "string" },
          "warnings": { "type": "array", "items": { "type": "string" } },
          "log":      { "type": "array", "items": { "type": "string" } }
        }
      }
    }
  }
}
""";

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task WriteJsonAsync(HttpListenerResponse res, object obj)
            => await WriteRawAsync(res,
                JsonConvert.SerializeObject(obj, Formatting.Indented), "application/json");

        private static async Task WriteRawAsync(
            HttpListenerResponse res, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = $"{contentType}; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.OutputStream.Close();
        }
    }

    // ── Status model ──────────────────────────────────────────────────────────

    public class GenerationStatus
    {
        [JsonProperty("running")] public bool Running { get; set; }
        [JsonProperty("step")] public string Step { get; set; } = "Idle";
        [JsonProperty("error")] public string Error { get; set; } = "";
        [JsonProperty("log")] public List<string> Log { get; set; } = new();
    }
}