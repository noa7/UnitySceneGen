# UnitySceneGen — Unity Scene Generator

Fully automated WPF + CLI + HTTP API tool. Generates a complete Unity project
and scene from a JSON config file with zero manual Unity Editor interaction.

---

## Table of Contents

1. [Architecture](#architecture)
2. [Build](#build)
3. [Modes of Operation](#modes-of-operation)
   - [WPF (GUI)](#wpf-gui)
   - [CLI](#cli)
   - [API Server](#api-server)
   - [Combined modes](#combined-modes)
4. [API Reference](#api-reference)
   - [Overview](#overview)
   - [GET /](#get-)
   - [GET /status](#get-status)
   - [GET /swagger](#get-swagger)
   - [GET /openapi.json](#get-openapiijson)
   - [POST /validate](#post-validate)
   - [POST /generate](#post-generate)
   - [POST /build](#post-build)
   - [Error responses](#error-responses)
   - [Concurrency model](#concurrency-model)
   - [Client timeout guidance](#client-timeout-guidance)
   - [CORS](#cors)
5. [Config Schema](#config-schema)
   - [Minimal example](#minimal-working-example)
   - [Transform — position, rotation, scale](#setting-position-rotation-and-scale)
   - [RectTransform](#setting-recttransform-properties)
   - [Custom scripts](#generating-and-attaching-custom-monobehaviour-scripts)
   - [Supported prop types](#supported-prop-value-types)
6. [How robustness is guaranteed](#how-robustness-is-guaranteed)
7. [Troubleshooting](#troubleshooting)

---

## Architecture

```
UnitySceneGen.exe  (WPF · CLI · API — single binary, three modes)
│
├── 1. Validate config JSON
├── 2. Scaffold project folder  (Assets/, ProjectSettings/, Packages/manifest.json)
├── 3. Write Builder.cs       → Assets/Editor/SceneGenerator/
├── 4. Write user scripts     → Assets/Scripts/   (from "scripts" block)
├── 5. Unity Pass 1  (-batchmode, no method)           → package import + compile
├── 6. Unity Pass 2  (-executeMethod UnitySceneGen.Builder.Run)  → build scene
└── 7. Read SceneGenResult.json → report success / failure / zip
         │
         └── Builder.cs  (runs inside Unity headless)
               ├── Reads SceneGenConfig.json from project root
               ├── Creates tags + layers via TagManager SerializedObject
               ├── Creates GameObjects in topological order
               ├── Adds components in priority order  (Canvas first)
               ├── Sets properties via reflection + SerializedObject
               └── Saves scene, writes SceneGenResult.json
```

---

## Build

**Requirements:** .NET 6 SDK · Windows

```cmd
cd UnitySceneGen
dotnet restore
dotnet build -c Release
```

Output: `bin\Release\net6.0-windows\UnitySceneGen.exe`

> The binary is self-contained — `Builder.cs` is embedded as a resource so
> there is nothing else to deploy alongside the `.exe`.

---

## Modes of Operation

### WPF (GUI)

Double-click `UnitySceneGen.exe` with no arguments.

- Fill in **Config JSON** — browse for a file, or paste directly in the Inline JSON tab
- Fill in **Unity Editor Executable** — defaults to `C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe` on first launch
- Fill in **Output Folder**
- Tick **--force** to delete and regenerate the project from scratch
- Click **▶ Generate**

All settings are saved automatically between sessions. The log panel streams
live Unity output. Explorer opens the output folder automatically on success.

---

### CLI

```cmd
UnitySceneGen.exe ^
  --config  MyScene.json ^
  --unity   "C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe" ^
  --out     D:\UnityProjects
```

| Argument | Required | Description |
|----------|----------|-------------|
| `--config <path>` | ✓ | Path to scene config JSON file |
| `--unity  <path>` | ✓ | Path to Unity.exe |
| `--out    <dir>`  | ✓ | Output directory for the Unity project |
| `--force`         |   | Delete and recreate the project from scratch |
| `--project <n>`   |   | Override the project name from the config |
| `--scene   <n>`   |   | Override the scene name from the config |
| `--help`          |   | Print usage |

**Exit codes:** `0` success · `1` config error · `2` IO error · `3` Unity exec error · `4` Builder error

---

### API Server

Start the HTTP API server by passing `--port`:

```cmd
UnitySceneGen.exe --port 5782
```

Console output:
```
[API] Listening  →  http://localhost:5782/
[API] Swagger UI →  http://localhost:5782/swagger
[API] OpenAPI    →  http://localhost:5782/openapi.json
[API] Generate   →  POST http://localhost:5782/generate
Running in API-only mode. Press Ctrl+C to stop.
```

Open **`http://localhost:5782/swagger`** in any browser for the interactive
Swagger UI where you can read the spec and execute requests directly.

**Port choice:** 5782 is well clear of all IANA registered and system-reserved
ports. Any port above 1024 and not already in use on your machine works —
just change the number in the command.

---

### Combined modes

`--port` can be combined with any other flag. The API server always starts
first on a background thread, then the foreground mode runs as normal.

```cmd
:: WPF window open + API server active simultaneously
UnitySceneGen.exe --port 5782

:: API server + one-shot CLI generation in the same process
UnitySceneGen.exe --port 5782 --config MyScene.json --unity "..." --out D:\Out
```

---

## API Reference

### Overview

The API server exposes **six routes**. All request and response bodies are
JSON (`Content-Type: application/json`) unless the endpoint explicitly returns
a different type (ZIP binary for `POST /generate`, HTML for `GET /swagger`).

All responses include CORS headers allowing requests from any origin.

| Method | Path | Returns | Purpose |
|--------|------|---------|---------|
| `GET` | `/` | JSON | Server info and endpoint list |
| `GET` | `/status` | JSON | Live status of the currently running job |
| `GET` | `/swagger` | HTML | Interactive Swagger UI |
| `GET` | `/openapi.json` | JSON | Raw OpenAPI 3.0 spec |
| `POST` | `/validate` | JSON | Validate a config without running Unity |
| `POST` | `/generate` | ZIP | Full project generation — runs Unity, returns `.zip` |
| `POST` | `/build` | JSON | WebGL build from a project ZIP, uploads to GCS |

> **Concurrency:** Unity's license system only allows **one headless instance
> at a time**. A single `SemaphoreSlim(1,1)` gate is shared across both
> `POST /generate` and `POST /build`. A second request while one is running
> receives `503 Service Unavailable` immediately. Poll `GET /status` while
> waiting, then retry.

---

### GET /

Returns a JSON object describing the running server.

**Request:** No body required.

**Response `200`:**

```json
{
  "service": "UnitySceneGen API",
  "version": "1.0.0",
  "port": 5782,
  "defaultUnityExePath": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "endpoints": [
    "GET  http://localhost:5782/swagger       — Swagger UI (browser)",
    "GET  http://localhost:5782/openapi.json  — OpenAPI 3.0 spec",
    "POST http://localhost:5782/generate      — Generate project, returns .zip"
  ]
}
```

**Use this endpoint** to confirm the server is up, discover the default Unity
path the server was started with, and enumerate available routes.

---

### GET /status

Returns a live snapshot of the currently running (or most recently completed)
generation or build job. Poll this endpoint while a `POST /generate` or
`POST /build` is in progress to stream log lines and track the current step.

**Request:** No body required.

**Response `200`:**

```json
{
  "running": true,
  "step": "Step 3/5 — Unity Pass 1 (compile)",
  "error": "",
  "log": [
    "[Step 1/5] Validating config…",
    "[Step 2/5] Setting up project…",
    "[Step 3/5] Starting Unity Pass 1…",
    "  [Unity] Importing package com.unity.textmeshpro…"
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `running` | `boolean` | `true` while a job is actively executing |
| `step` | `string` | Human-readable label of the current pipeline step |
| `error` | `string` | Last error message. Empty string when no error has occurred |
| `log` | `string[]` | All log lines emitted since the job started, including Unity stdout |

**Step values for `POST /generate`:**

| `step` value | Meaning |
|---|---|
| `"Starting…"` | Job accepted, initialising |
| `"Step 1/5 — Validating config"` | Config JSON is being parsed and validated |
| `"Step 2/5 — Setting up project"` | Project folder scaffold is being written |
| `"Step 3/5 — Unity Pass 1 (compile)"` | Unity headless — importing packages and compiling |
| `"Step 4/5 — Unity Pass 2 (scene build)"` | Unity headless — executing `Builder.Run`, building the scene |
| `"Step 5/5 — Writing summary"` | Reading `SceneGenResult.json`, preparing ZIP |
| `"Failed"` | Job ended with an error (see `error` field) |

**Step values for `POST /build`:**

| `step` value | Meaning |
|---|---|
| `"Build: receiving source…"` | Reading request body |
| `"Build: extracting source…"` | Unpacking the source ZIP |
| `"Build: injecting build script…"` | Writing `WebGLBuilder.cs` into the project |
| `"Build: injecting TMP importer…"` | Writing `TmpAutoImporter.cs` |
| `"Build: Unity building WebGL…"` | Unity headless WebGL build running |
| `"Build: uploading to GCS…"` | Uploading output to Google Cloud Storage |
| `"Build: complete ✓"` | Job succeeded |
| `"Build: FAILED"` | Job ended with an error |

**Polling example:**

```bash
# Poll every 5 seconds while running=true
while true; do
  STATUS=$(curl -s http://localhost:5782/status)
  RUNNING=$(echo $STATUS | python -c "import sys,json; print(json.load(sys.stdin)['running'])")
  STEP=$(echo $STATUS | python -c "import sys,json; print(json.load(sys.stdin)['step'])")
  echo "[$STEP]"
  if [ "$RUNNING" = "False" ]; then break; fi
  sleep 5
done
```

---

### GET /swagger

Serves the Swagger UI HTML page. Open in a browser to get a fully interactive
API explorer with the request example pre-filled and a Download link for the
returned ZIP.

**Request:** No body required. Open in a browser.

**Note:** During a `POST /generate` or `POST /build` run, the Swagger UI will
show a spinner because the connection is held open for the full Unity pipeline
(5–20 minutes). Do not click Execute a second time.

---

### GET /openapi.json

Returns the raw OpenAPI 3.0 specification as JSON.

**Request:** No body required.

Import into Postman, Insomnia, or any other API client with:
- Postman: Import → Link → `http://localhost:5782/openapi.json`
- Insomnia: Create → Import → URL → `http://localhost:5782/openapi.json`

---

### POST /validate

Validates a scene config against all UnitySceneGen rules **without** running
Unity. Use this to catch config errors cheaply before committing to a full
generation run (which takes 5–20 minutes).

**Request body** (`application/json`):

```json
{
  "config": {
    "project":  { "name": "MyProject", "unityVersion": "6000.0.3f1" },
    "settings": { "tags": ["Player"], "layers": ["Gameplay"] },
    "scenes":   [{ "name": "Main", "path": "Assets/Scenes/Main.unity", "roots": ["go.root"] }],
    "gameObjects": [
      { "id": "go.root", "name": "Root" }
    ]
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `config` | ✓ | The scene config object to validate (same schema as `POST /generate`) |

**Responses:**

| Status | Body | When |
|--------|------|------|
| `200` | `{ "valid": true, "errors": [], "warnings": [] }` | Config is valid |
| `422` | `{ "valid": false, "errors": ["..."], "warnings": ["..."] }` | Config has validation errors |
| `400` | `{ "error": "..." }` | Request JSON could not be parsed, or `config` field missing |

**Response body fields:**

| Field | Type | Description |
|-------|------|-------------|
| `valid` | `boolean` | `true` only when `errors` is empty |
| `errors` | `string[]` | Fatal validation errors that would prevent generation |
| `warnings` | `string[]` | Non-fatal issues that generation will proceed despite |

**Example — valid config:**
```json
{ "valid": true, "errors": [], "warnings": [] }
```

**Example — invalid config:**
```json
{
  "valid": false,
  "errors": [
    "Scene 'Main' references root id 'go.missing' which is not defined in gameObjects"
  ],
  "warnings": [
    "GameObject 'go.root' has no components — it will be an empty Transform"
  ]
}
```

---

### POST /generate

Runs the full two-pass Unity generation pipeline and returns the completed
Unity project as a ZIP archive.

> ⚠️ **This request holds the HTTP connection open** for the entire Unity run.
> Set your HTTP client timeout to at least **30 minutes** (`1800` seconds).
> Poll `GET /status` in a separate request to track progress while waiting.

**Request body** (`application/json`):

```json
{
  "unityExePath": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "force": false,
  "config": {
    "project":  { "name": "MyProject", "unityVersion": "6000.0.3f1" },
    "settings": { "tags": ["Player"], "layers": ["Gameplay"] },
    "scenes":   [{ "name": "Main", "path": "Assets/Scenes/Main.unity", "roots": ["go.root"] }],
    "gameObjects": [
      { "id": "go.root", "name": "Root", "children": ["go.cam"] },
      {
        "id": "go.cam",
        "name": "Main Camera",
        "components": [
          { "type": "UnityEngine.Camera" },
          { "type": "UnityEngine.AudioListener" }
        ]
      }
    ]
  }
}
```

**Request fields:**

| Field | Required | Type | Default | Description |
|-------|----------|------|---------|-------------|
| `config` | ✓ | `object` | — | Full UnitySceneGen scene config. Same schema as the `.json` file — see [Config Schema](#config-schema) |
| `unityExePath` | | `string` | Server default path | Absolute path to `Unity.exe` on the **server** machine. Omit to use the server's default (shown in `GET /`) |
| `force` | | `boolean` | `false` | When `true`, deletes and recreates the project folder from scratch before generating |

**Responses:**

| Status | Content-Type | Body | When |
|--------|-------------|------|------|
| `200` | `application/zip` | Binary ZIP of the project folder | Generation succeeded |
| `400` | `application/json` | `{ "error": "...", "hint": "...", "defaultPath": "..." }` | `config` missing, JSON malformed, or `Unity.exe` not found |
| `422` | `application/json` | `{ "error": "...", "warnings": [...], "log": [...] }` | Config valid but Unity pipeline failed |
| `503` | `application/json` | `{ "error": "...", "status": "busy" }` | Another job is already running |
| `500` | `application/json` | `{ "error": "..." }` | Unexpected server exception |

**Response headers on `200`:**

| Header | Description |
|--------|-------------|
| `Content-Disposition` | `attachment; filename="<projectName>.zip"` |
| `X-Warnings` | Pipe-separated (`\|`) builder warnings. Empty string when none |

**`422` body fields:**

| Field | Type | Description |
|-------|------|-------------|
| `error` | `string` | The failure reason |
| `warnings` | `string[]` | Non-fatal warnings emitted before the failure |
| `log` | `string[]` | Full generation log, including all Unity stdout lines |

**`400` body fields (Unity not found):**

| Field | Type | Description |
|-------|------|-------------|
| `error` | `string` | Description of what was not found |
| `hint` | `string` | Actionable suggestion |
| `defaultPath` | `string` | The path the server tried when `unityExePath` was omitted |

**curl example — save ZIP to disk:**

```cmd
curl -X POST http://localhost:5782/generate ^
  -H "Content-Type: application/json" ^
  -d @MyScene.json ^
  --output MyProject.zip ^
  --max-time 1800
```

**Python example — poll status while waiting:**

```python
import requests, threading, time, json

BASE = "http://localhost:5782"

def poll_status():
    while True:
        r = requests.get(f"{BASE}/status")
        s = r.json()
        print(f"[{s['step']}]", flush=True)
        if not s["running"]:
            break
        time.sleep(5)

# Start polling in background
t = threading.Thread(target=poll_status, daemon=True)
t.start()

# Fire the generate request (long-running, keep timeout high)
with open("MyScene.json") as f:
    config = json.load(f)

resp = requests.post(f"{BASE}/generate", json={"config": config}, timeout=1800)

if resp.status_code == 200:
    with open("MyProject.zip", "wb") as f:
        f.write(resp.content)
    warnings = resp.headers.get("X-Warnings", "")
    print("Done. Warnings:", warnings or "none")
else:
    print("Failed:", resp.json())
```

---

### POST /build

Accepts a Unity project as a base64-encoded ZIP, runs a WebGL build inside
Unity headless, and optionally uploads the output to Google Cloud Storage.
Returns a JSON result with a public URL.

> ⚠️ **This request also holds the HTTP connection open** for the full Unity
> WebGL build (can take 15–30 minutes for large projects). Set your HTTP
> client timeout accordingly, and poll `GET /status` for progress.

**Request body** (`application/json`):

```json
{
  "projectZipBase64": "<base64-encoded ZIP of the Unity project folder>",
  "projectName": "MyProject",
  "unityExePath": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "gcsBucket": "my-gcs-bucket",
  "gcsKeyJson": "<base64-encoded Google service account JSON key>",
  "development": false
}
```

**Request fields:**

| Field | Required | Type | Default | Description |
|-------|----------|------|---------|-------------|
| `projectZipBase64` | ✓ | `string` | — | Base64-encoded ZIP archive of the Unity project. The ZIP must contain a top-level folder named `<projectName>` |
| `projectName` | ✓ | `string` | — | Name of the top-level folder inside the ZIP, and the GCS upload prefix |
| `unityExePath` | | `string` | Server default | Absolute path to `Unity.exe` on the server machine |
| `gcsBucket` | | `string` | `"aqe-unity-builds"` | GCS bucket name to upload the WebGL build to |
| `gcsKeyJson` | | `string` | — | Base64-encoded Google service account JSON key. When omitted, GCS upload is skipped |
| `development` | | `boolean` | `false` | When `true`, builds with `BuildOptions.Development` (enables profiler, script debugging) |

**What this endpoint does internally:**

1. Decodes and extracts the source ZIP to a temp folder
2. Injects `TmpAutoImporter.cs` — auto-imports TextMeshPro Essentials if not already present
3. Injects `WebGLBuilder.cs` — configures WebGL settings and calls `BuildPipeline.BuildPlayer`
4. Runs Unity headless: `-batchmode -quit -executeMethod WebGLBuilder.Build`
5. If `gcsKeyJson` is provided, uploads the WebGL output folder to `gs://<gcsBucket>/<projectName>/` using `gsutil`
6. Returns the public URL of `index.html`

**WebGL build settings applied automatically:**

| Setting | Value |
|---------|-------|
| Memory size | 1024 MB |
| Compression format | Disabled |
| Data caching | Enabled |
| Decompression fallback | Enabled |
| Scenes | All `.unity` files found under `Assets/` |

**Responses:**

| Status | Body | When |
|--------|------|------|
| `200` | `{ "success": true, "url": "https://...", "warnings": [], "log": [...] }` | Build succeeded |
| `422` | `{ "success": false, "error": "...", "log": [...] }` | Build failed |
| `400` | `{ "error": "..." }` | Required fields missing or `Unity.exe` not found |
| `503` | `{ "error": "...", "status": "busy" }` | Another job is already running |

**`200` response fields:**

| Field | Type | Description |
|-------|------|-------------|
| `success` | `boolean` | Always `true` on `200` |
| `url` | `string` | Public URL of `index.html` in GCS. Empty string when GCS upload was skipped |
| `warnings` | `string[]` | Non-fatal warnings (currently always empty) |
| `log` | `string[]` | Full build log including Unity stdout |

**Example — build and upload to GCS:**

```python
import requests, base64, json

BASE = "http://localhost:5782"

with open("MyProject.zip", "rb") as f:
    zip_b64 = base64.b64encode(f.read()).decode()

with open("service-account.json", "rb") as f:
    key_b64 = base64.b64encode(f.read()).decode()

resp = requests.post(f"{BASE}/build", json={
    "projectZipBase64": zip_b64,
    "projectName": "MyProject",
    "gcsBucket": "my-builds-bucket",
    "gcsKeyJson": key_b64,
    "development": False,
}, timeout=2400)

result = resp.json()
if result["success"]:
    print("Live at:", result["url"])
else:
    print("Failed:", result["error"])
    print("\n".join(result["log"][-20:]))
```

**GCS prerequisite:** `gsutil` must be installed and accessible on `PATH` on
the server machine. If `gsutil` is not available, the upload step is silently
skipped and `url` in the response will still be set to the expected GCS URL
(but the files will not have been uploaded).

---

### Error Responses

All error responses share a common JSON shape:

```json
{
  "error": "Human-readable description of what went wrong",
  "hint": "Actionable suggestion (only present on some 400 errors)",
  "defaultPath": "C:\\...\\Unity.exe (only present when exe not found)",
  "warnings": ["Non-fatal warning 1", "..."],
  "log": ["Log line 1", "Log line 2", "..."]
}
```

| Field | Present on | Description |
|-------|-----------|-------------|
| `error` | All error responses | Human-readable error message |
| `hint` | `400` when `unityExePath` not found | Actionable suggestion |
| `defaultPath` | `400` when `unityExePath` not found | Path the server tried |
| `warnings` | `422` | Non-fatal builder warnings |
| `log` | `422` | Full generation/build log lines |
| `status` | `503` | Always `"busy"` — signals retry is appropriate |

---

### Concurrency Model

The server enforces that **only one Unity headless job runs at a time**, because Unity's
license system does not allow concurrent `-batchmode` instances.

This constraint covers both `POST /generate` and `POST /build` — they share a
single `SemaphoreSlim(1,1)` gate.

**Behavior when busy:**
- The busy check uses `WaitAsync(0)` — it is non-blocking and returns `503` immediately without queueing the request.
- The caller must retry after the current job finishes.
- Use `GET /status` to determine when `running` becomes `false`.

**Retry pattern:**

```python
import requests, time

def generate_with_retry(payload, base="http://localhost:5782", max_attempts=10):
    for attempt in range(max_attempts):
        resp = requests.post(f"{base}/generate", json=payload, timeout=1800)
        if resp.status_code == 503:
            print(f"Server busy — waiting 30s (attempt {attempt+1}/{max_attempts})")
            time.sleep(30)
            continue
        return resp
    raise RuntimeError("Server remained busy after all retry attempts")
```

---

### Client Timeout Guidance

| Endpoint | Typical duration | Recommended client timeout |
|----------|-----------------|---------------------------|
| `GET /` | < 1s | 10s |
| `GET /status` | < 1s | 10s |
| `POST /validate` | < 1s | 10s |
| `POST /generate` | 5–20 min | 1800s (30 min) |
| `POST /build` | 15–30 min | 2400s (40 min) |

Unity pass timeouts enforced server-side:
- Pass 1 (compile + package import): **12 minutes**
- Pass 2 (scene build): **8 minutes**
- WebGL build (`POST /build`): **20 minutes**

---

### CORS

All responses include:

```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Content-Type
```

`OPTIONS` preflight requests return `204 No Content`. Browser-based clients
(including the Swagger UI) can call all endpoints without additional configuration.

---

## Config Schema

### Minimal working example

```json
{
  "project":  { "name": "MyProject", "unityVersion": "6000.0.3f1" },
  "settings": { "tags": ["Player"], "layers": ["Gameplay"] },
  "scenes":   [{ "name": "Main", "path": "Assets/Scenes/Main.unity", "roots": ["go.root"] }],
  "gameObjects": [
    { "id": "go.root", "name": "Root", "children": ["go.cam"] },
    {
      "id": "go.cam",
      "name": "Main Camera",
      "components": [
        { "type": "UnityEngine.Camera" },
        { "type": "UnityEngine.AudioListener" }
      ]
    }
  ]
}
```

### Top-level fields

| Field | Required | Description |
|-------|----------|-------------|
| `project` |  | Project name, Unity version, extra packages |
| `settings` |  | Tags and layers to register before any GameObject is created |
| `scripts` |  | Custom MonoBehaviour scripts to generate and compile |
| `scenes` | ✓ | One or more scenes to build |
| `gameObjects` | ✓ | All GameObjects referenced by any scene |

### Project fields

| Field | Default | Description |
|-------|---------|-------------|
| `name` | `"MyProject"` | Unity project name and output folder name |
| `unityVersion` | `"2022.3.20f1"` | Unity version string. Must match an installed Unity Editor version on the server |
| `packages` | `[]` | Extra UPM package IDs to add to `Packages/manifest.json`, e.g. `"com.unity.textmeshpro"` |

### Settings fields

| Field | Default | Description |
|-------|---------|-------------|
| `tags` | `[]` | Custom tags to register. Unity built-ins (`Untagged`, `Respawn`, `Finish`, `EditorOnly`, `MainCamera`, `Player`, `GameController`) are always available |
| `layers` | `[]` | Custom layers to register. Unity built-ins (`Default`, `TransparentFX`, `Ignore Raycast`, `Water`, `UI`) are always available. Maximum 24 user-defined layers |

### Scene fields

| Field | Required | Description |
|-------|----------|-------------|
| `name` | ✓ | Scene display name |
| `path` | ✓ | Output path within the project, e.g. `"Assets/Scenes/Main.unity"` |
| `roots` | ✓ | List of root-level GameObject `id` values for this scene |

### GameObject fields

| Field | Default | Description |
|-------|---------|-------------|
| `id` | — | Unique string identifier used for parent/child references and `"ref:<id>"` props |
| `name` | `"GameObject"` | Display name in the Hierarchy |
| `active` | `true` | Sets `GameObject.SetActive()` |
| `tag` | `"Untagged"` | Must be listed in `settings.tags` or be a Unity built-in tag |
| `layer` | `"Default"` | Must be listed in `settings.layers` or be a Unity built-in layer |
| `children` | `[]` | List of child GameObject `id` values |
| `components` | `[]` | Components to add — each has `type` and optional `props` |

### Component fields

| Field | Required | Description |
|-------|----------|-------------|
| `type` | ✓ | Fully qualified Unity type name, e.g. `"UnityEngine.Camera"`. Custom scripts use the class name (with optional namespace), e.g. `"MyGame.Utils.Rotator"` |
| `props` | | Key-value map of properties to set on the component after it is added |

---

### Setting position, rotation and scale

`UnityEngine.Transform` is a regular component entry. The builder never adds
it (every GameObject already has one) — it simply applies the props to the
existing Transform via reflection.

```json
{
  "id": "go.player",
  "name": "Player",
  "tag": "Player",
  "layer": "Gameplay",
  "components": [
    {
      "type": "UnityEngine.Transform",
      "props": {
        "localPosition":    [0, 1, 0],
        "localEulerAngles": [0, 45, 0],
        "localScale":       [1, 1, 1]
      }
    },
    { "type": "UnityEngine.Rigidbody",      "props": { "mass": 1.0, "useGravity": true } },
    { "type": "UnityEngine.CapsuleCollider" }
  ]
}
```

> All Transform props use **local space** — a child's `localPosition` is
> relative to its parent, exactly as shown in the Unity Inspector.

---

### Setting RectTransform properties

`UnityEngine.RectTransform` works the same way as Transform. All anchor,
offset, size and pivot properties are public and resolved via reflection.

```json
{
  "id": "go.panel",
  "name": "Panel",
  "layer": "UI",
  "components": [
    {
      "type": "UnityEngine.RectTransform",
      "props": {
        "anchorMin":        [0.1, 0.2],
        "anchorMax":        [0.9, 0.8],
        "offsetMin":        [0, 0],
        "offsetMax":        [0, 0],
        "anchoredPosition": [0, 0],
        "sizeDelta":        [200, 50],
        "pivot":            [0.5, 0.5]
      }
    },
    { "type": "UnityEngine.UI.Image", "props": { "color": "#00000080" } }
  ]
}
```

> **Canvas must come before RectTransform.** Adding `UnityEngine.Canvas`
> automatically promotes the plain Transform to a RectTransform.
> `ComponentPriority()` in Builder.cs ensures Canvas is always added first,
> so the RectTransform is always present when its props are applied.

| Property | Type | Description |
|---|---|---|
| `anchorMin` | Vector2 | Bottom-left anchor (0–1 each axis) |
| `anchorMax` | Vector2 | Top-right anchor (0–1 each axis) |
| `anchoredPosition` | Vector2 | Position relative to the anchor pivot |
| `sizeDelta` | Vector2 | Width/height when anchors are the same point |
| `offsetMin` | Vector2 | Pixel offset from the bottom-left anchor |
| `offsetMax` | Vector2 | Pixel offset from the top-right anchor |
| `pivot` | Vector2 | Pivot point (`[0.5, 0.5]` = centre) |
| `localPosition` | Vector3 | 3-D local position (inherited from Transform) |
| `localEulerAngles` | Vector3 | Local rotation in degrees |
| `localScale` | Vector3 | Local scale |

---

### Generating and attaching custom MonoBehaviour scripts

Declare scripts in the top-level `"scripts"` array. The tool writes each one
to `Assets/Scripts/<n>.cs` **before Unity Pass 1 runs**, so they are fully
compiled and available to attach as components in Pass 2.

```json
{
  "scripts": [
    {
      "name": "Rotator",
      "body": "    [SerializeField] float speed = 90f;\n\n    void Update()\n    {\n        transform.Rotate(Vector3.up * speed * Time.deltaTime);\n    }"
    },
    {
      "name": "OscillateY",
      "namespace": "MyGame.Utils",
      "body": "    [SerializeField] float amplitude = 0.5f;\n    [SerializeField] float frequency = 1.5f;\n    private float _originY;\n\n    void Start()  { _originY = transform.position.y; }\n\n    void Update()\n    {\n        var p = transform.position;\n        p.y = _originY + Mathf.Sin(Time.time * frequency) * amplitude;\n        transform.position = p;\n    }"
    }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | ✓ | Class name and `.cs` filename. Must be a valid C# identifier |
| `body` |  | C# source for the class body. UnitySceneGen wraps it in `public class <n> : MonoBehaviour { }` automatically. Leave empty for a stub with `Start()` / `Update()` |
| `namespace` |  | Optional namespace. When set, reference the component as `"MyGame.Utils.OscillateY"` in `components` |

Attach a generated script exactly like any built-in component:

```json
{
  "id": "go.coin",
  "name": "Coin",
  "components": [
    { "type": "UnityEngine.SphereCollider", "props": { "isTrigger": true } },
    { "type": "Rotator" },
    { "type": "MyGame.Utils.OscillateY" }
  ]
}
```

`[SerializeField]` default values in the script body are captured by Unity's
serializer at `AddComponent` time and written into the `.unity` file
automatically — no extra `props` needed.

---

### Supported prop value types

| Type | Format | Example |
|------|--------|---------| 
| bool | `true` / `false` | `true` |
| int | number | `42` |
| float | number | `60.0` |
| string | string | `"hello"` |
| enum | string name | `"ScreenSpaceOverlay"` |
| Color | `#RRGGBBAA` | `"#FF000080"` |
| Vector2 | `[x, y]` | `[1920, 1080]` |
| Vector3 | `[x, y, z]` | `[0, 1, 0]` |
| Vector4 | `[x, y, z, w]` | `[0, 0, 0, 1]` |
| ref | `"ref:<goId>"` | `"ref:go.camera"` |

Property lookup order inside the builder (first match wins):
1. Public C# property with a setter (reflection)
2. Public C# field (reflection)
3. Serialized private field via `SerializedObject`

---

## How robustness is guaranteed

| Problem | Solution |
|---------|----------|
| Unity exit codes unreliable | Builder writes `SceneGenResult.json`; orchestrator reads that and ignores the exit code |
| TMP not ready on first launch | Two-pass Unity: Pass 1 imports packages, Pass 2 builds the scene |
| User scripts not compiled in time | Scripts written to `Assets/Scripts/` before Pass 1 so they compile with the project |
| `GetComponent` returning Unity fake-null | Explicit `if (comp == null)` check instead of `??` operator in `AddComponents` |
| Unity silent hang / license error | Process killed if no log output for 120 s |
| Hard timeout | Pass 1 = 12 min, Pass 2 = 8 min |
| RectTransform ordering | `ComponentPriority()` sorts Canvas → RectTransform → rest |
| Tag/layer race condition | Tags/layers created and saved before any GameObject is created |
| Concurrent API requests | `SemaphoreSlim(1,1)` gate — second caller gets `503` immediately |
| API response never received | `res.OutputStream.Close()` called after writing ZIP bytes to finalise the HTTP response |

---

## Troubleshooting

**Builder.Run never called**
Compilation error in `Builder.cs` or a user script. Check `SceneGenUnity_Pass2.log` in the project folder.

**TextMeshPro type not found**
TMP is still importing. Increase `Pass1Timeout` in `UnityLauncher.cs` (default 12 min).

**Tag or layer not applied**
The tag/layer must be declared in `settings.tags` / `settings.layers`. Unity built-in tags (`Untagged`, `MainCamera`, etc.) and layers (`Default`, `UI`, etc.) are always available without declaring them.

**"No empty layer slot"**
Unity supports a maximum of 24 user-defined layers. Remove unused layers in Project Settings or reduce the number in the config.

**Custom script type not found**
Confirm the `name` and `namespace` in the `scripts` block exactly match the `type` string used in `components`. Check `SceneGenUnity_Pass1.log` for compile errors in the generated script. Re-run with `--force` after fixing the script body.

**Transform props ignored**
The type must be the fully qualified `"UnityEngine.Transform"`, not the short form `"Transform"`. The type resolver requires the namespace prefix.

**API server not starting / address already in use**
Another process is bound to that port. Pick a different port: `--port 5999`.

**API returns 503 immediately**
A generation job is already running. Poll `GET /status` until `running` is `false`, then retry.

**Swagger UI shows spinner indefinitely**
This is normal during a generation run — the connection is held open for the full Unity pipeline (5–20 min depending on machine speed). Do not click Execute a second time. If it never resolves, check the server console for errors and confirm Unity.exe is running in Task Manager.

**ZIP download does not appear in Swagger UI**
Some browser security policies block binary downloads from localhost. Use curl instead:
```cmd
curl -X POST http://localhost:5782/generate ^
  -H "Content-Type: application/json" ^
  -d @MyScene.json ^
  --output MyProject.zip ^
  --max-time 1800
```

**POST /build — GCS upload skipped silently**
`gsutil` must be installed on the server machine and available on `PATH`. Verify with `gsutil version` in a terminal on the server. If `gsutil` is missing, the build itself still completes successfully but files are not uploaded.