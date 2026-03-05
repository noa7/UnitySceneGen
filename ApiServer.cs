using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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