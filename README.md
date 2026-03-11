# UnitySceneGen — Internal Architecture

> **Audience:** Developers who want to understand how the codebase works, extend it, or debug it.
> **File:** Everything lives in a single file: `Program.cs` (~3100 lines), divided into 12 numbered sections.

---

## Overview

UnitySceneGen is a Windows desktop application (WPF) that generates a complete Unity project from a declarative ZIP input. It can run in two modes:

- **GUI mode** — a WPF window where a user picks files and clicks Generate.
- **API-only mode** (`--api-only` flag) — a headless HTTP server with no UI.

In both modes, an `ApiServer` is always started. In GUI mode it runs as a background thread alongside the WPF app. They share a single `ProcessingGate` that enforces one-job-at-a-time.

The end-to-end pipeline is: **ZIP in → Unity project folder on disk → zipped Unity project out**.

---

## Section Map

| Section | Class(es) | Purpose |
|---|---|---|
| 1 | Models | All data contracts (config, results, status) |
| 2 | `AppSettings` | Persistent user settings (JSON in AppData) |
| 2b | `ProcessingGate` | One-slot semaphore shared by GUI + API |
| 3 | `ZipLoader` | Extracts ZIP, locates `scene.json`, discovers scripts |
| 4 | `TemplateResolver` | Expands template shortcuts into full component lists |
| 5 | `ConfigValidator` | Validates the resolved config before touching Unity |
| 6 | `ProjectCreator` | Scaffolds the Unity project folder on disk |
| 7 | `UnityLauncher` | Spawns Unity in batch mode, tails its log, detects success/failure |
| 8 | `GenerationEngine` | Orchestrates steps 1–6 of the pipeline |
| 9 | `ComponentSchema` | Runtime catalog of all known Unity component types |
| 10 | `ApiServer` | HTTP server (HttpListener), all REST endpoints |
| 11 | `MainWindow` | WPF UI (code-behind only, no XAML) |
| 12 | `Program` | Entry point, CLI arg parsing, mode selection |

---

## Data Flow

```
scene.zip (bytes)
     │
     ▼
[Section 3] ZipLoader.Load()
     ├─ Extracts to temp dir (GUID-named)
     ├─ Finds scene.json (root or one folder deep)
     ├─ Deserialises scene.json → SceneGenConfig
     └─ Auto-discovers scripts/*.cs → SceneGenConfig.Scripts
     │
     ▼
[Section 4] TemplateResolver.Resolve()
     ├─ For each GameObject with a "template" field:
     │   ├─ Looks up built-in templates (e.g. "physics/rigidbody", "ui/button")
     │   │   OR file-based templates from templates/*.json in the ZIP
     │   ├─ Deep-copies template components as the base
     │   ├─ Applies templateProps via propMappings
     │   └─ Merges any explicit components on top (explicit wins)
     └─ Clears Template + TemplateProps fields after expansion
     │
     ▼
[Section 5] ConfigValidator.ValidateConfig()
     ├─ Checks project name, at least one scene
     ├─ Unique + non-empty GameObject IDs
     ├─ Dangling child/root references
     ├─ Cycle detection (DFS)
     ├─ Tags + layers declared in settings
     ├─ Script names are valid C# identifiers
     └─ Returns ValidationResult { Valid, Errors[], Warnings[] }
     │
     ▼  (abort if !Valid)
     │
     ▼
[Section 6] ProjectCreator.CreateOrVerify()
     ├─ Creates: Assets/, Assets/Scenes/, Assets/Scripts/,
     │           Assets/Editor/SceneGenerator/, Packages/, ProjectSettings/
     ├─ Writes ProjectSettings/ProjectVersion.txt (Unity version)
     ├─ Writes Packages/manifest.json (all 31 built-in modules + ugui + newtonsoft)
     ├─ Copies each .cs from SceneGenConfig.Scripts → Assets/Scripts/
     ├─ Writes SceneGenConfig.json (template-resolved config, no scripts list)
     └─ Writes Builder.cs → Assets/Editor/SceneGenerator/
         (extracted from embedded resource or adjacent file)
     │
     ▼
[Section 7] UnityLauncher.Pass1ImportAsync()
     ├─ Spawns: Unity.exe -batchmode -quit -nographics
     │          -projectPath <path> -logFile SceneGenUnity_Pass1.log
     ├─ Tails the log file every 500ms, emits lines via the log callback
     ├─ Hang detection: kills if no new log output for 120s
     ├─ Timeout: 12 minutes hard limit
     └─ Success = absence of failure patterns (Unity 6 exits 1 even on success)
         Failure patterns: "compilation errors", "error CS", etc.
     │
     ▼
[Section 7] UnityLauncher.Pass2BuildAsync()
     ├─ Spawns: Unity.exe ... -executeMethod UnitySceneGen.Builder.Run
     ├─ Builder.cs reads SceneGenConfig.json, creates scenes + GameObjects
     ├─ Builder writes SceneGenResult.json on completion
     ├─ Timeout: 8 minutes
     └─ Returns BuilderResult { Success, Error, Warnings[], Scenes[] }
     │
     ▼
Output: Unity project folder on disk
        (optionally zipped and returned to API caller)
```

---

## Key Classes

### `ProcessingGate` (Section 2b)

A non-blocking, one-slot semaphore. The single shared instance is passed to both `MainWindow` and `ApiServer`. Whoever calls `TryAcquire()` first gets an `IDisposable` token; the other caller gets `null` immediately (no waiting, no queuing). Disposing the token releases the slot and fires `BusyChanged`.

```
gate.TryAcquire() → IDisposable token   (success, you own the slot)
gate.TryAcquire() → null                (already busy)
```

### `ZipLoader` (Section 3)

Single static entry point: `ZipLoader.Load(byte[] zipBytes, Action<string>? log)`. Extracts to `%TEMP%\UnitySceneGen_<GUID>`. The caller is responsible for deleting the temp dir when done — `GenerationEngine` does this in its `finally` block.

**ZIP Contract:**
```
scene.zip
├── scene.json           ← required; at root or one folder deep
├── scripts/             ← optional; .cs files auto-discovered recursively
│   └── MyScript.cs
└── templates/           ← optional; .json template definition files
    └── my_template.json
```

`scene.json` must never contain inline C# code or file path references. Scripts are exclusively physical `.cs` files in `scripts/`.

### `TemplateResolver` (Section 4)

Expands the `template` shorthand on a `GameObjectConfig` into a full `components` list. Comes with 8 built-in templates:

| Template key | What it creates |
|---|---|
| `basic/transform` | Transform only |
| `basic/mesh` | MeshRenderer + MeshFilter + Transform |
| `ui/canvas` | Canvas + CanvasScaler + GraphicRaycaster |
| `ui/button` | RectTransform + Image + Button + TextMeshProUGUI |
| `ui/label` | RectTransform + TextMeshProUGUI |
| `physics/rigidbody` | Rigidbody + BoxCollider |
| `physics/trigger` | BoxCollider (isTrigger=true) |
| `audio/source` | AudioSource |

Custom file-based templates are loaded from `templates/*.json` in the ZIP. Their schema matches `TemplateDefinition`: `name`, `description`, `components[]`, `propMappings{}`.

`PropMappings` map a friendly key name to `"ComponentType.propName"`. Example:
```json
"propMappings": { "label": "TMPro.TextMeshProUGUI.text" }
```

### `ConfigValidator` (Section 5)

Validates after template resolution. Run order:
1. Project name present
2. At least one scene, each with name + path
3. All GameObject IDs unique and non-empty
4. No dangling child/root references
5. Cycle detection (DFS across the `children` graph)
6. ID naming convention warnings (children should start with `parentId.`)
7. Unresolved templates (left-over `template` field = error)
8. Component prop validation (color format, `ref:` references, array element count)
9. Tags + layers declared in settings
10. Script names are valid C# identifiers, no duplicates
11. Namespace-free component type not in `scripts/` (warning)

**Errors block the pipeline. Warnings do not.**

### `ProjectCreator` (Section 6)

Creates the Unity project structure on disk. If the project folder already exists and `force=false`, it is reused (idempotent re-runs are supported — only `Builder.cs`, user scripts, and `SceneGenConfig.json` are always refreshed). If `force=true`, the existing folder is deleted first.

`Builder.cs` is extracted from an embedded assembly resource (`UnitySceneGen.UnityBuilder.Builder.cs`) or, failing that, from a `Builder.cs` file adjacent to the executable.

### `UnityLauncher` (Section 7)

All Unity interaction goes through here. Unity is always run in `-batchmode -nographics`. Two passes:

**Pass 1** — import packages + compile scripts. No `-executeMethod`. Success is determined by the *absence* of failure patterns in the log (Unity 6 exits with code 1 even on success, so exit code alone cannot be trusted).

**Pass 2** — run `Builder.Run` via `-executeMethod`. `Builder.cs` reads `SceneGenConfig.json`, builds the scene hierarchy, and writes `SceneGenResult.json`. Pass 2 success requires `SceneGenResult.json` to exist and `success: true`.

The log tailer reads the Unity log file every 500ms using `FileShare.ReadWrite` and emits new lines via the log callback. This is how callers see Unity's own log output in real time.

### `GenerationEngine` (Section 8)

Thin orchestrator. Calls the pipeline in order, short-circuits on any failure, and always cleans up the temp extract directory. Exposes two public methods:

- `Run(zipBytes, unityExe, outputDir, force, log, ct)` — full 6-step pipeline
- `ValidateZip(zipBytes)` — steps 1–3 only (no Unity, fast)

### `ApiServer` (Section 10)

Built on `System.Net.HttpListener`. Starts in a background `Task.Run` loop. Each request is handled in its own task (fire-and-forget from the accept loop). CORS headers (`*`) are set on every response.

The `UiLog` property (type `Action<string>?`) is set by `MainWindow` to forward API job log lines into the WPF log viewer.

---

## Log Line Conventions

All log lines are prefixed by their originating component:

| Prefix | Source |
|---|---|
| `[ZipLoader]` | ZIP extraction and script discovery |
| `[ProjectCreator]` | File system operations |
| `[Unity Pass 1]` | Unity import/compile phase |
| `[Unity Pass 2]` | Unity scene-build phase |
| `[Unity]` | Raw lines tailed from Unity's log file |
| `[API]` / `[API/upload]` | API server lifecycle messages |
| `[Build]` | POST /build handler |
| `[GCS]` | Google Cloud Storage upload |
| `=== Step N/6: ... ===` | Pipeline stage markers |
| `  ✓ ...` | Success confirmation |
| `  [WARN] ...` | Warning — uppercase, non-fatal |
| `[ERROR] ...` | Error — uppercase, pipeline will abort |

---

## Startup Modes

```
UnitySceneGen.exe                          → GUI + background API on port 50831
UnitySceneGen.exe --api-only               → headless API on port 50831
UnitySceneGen.exe --api-only --port 9000   → headless API on port 9000
```

In GUI mode, if the debugger is attached (`Debugger.IsAttached`), `ApiServer.Start()` is a no-op — the listener is never started. This prevents port conflicts during development.

---

## Error Handling Philosophy

- All exceptions from user-controlled data (ZIP content, JSON) are caught and converted to `GenerationResult { Success=false, Error=... }` or HTTP 400/422.
- Unhandled exceptions from the HTTP handler are caught at the `HandleAsync` level and returned as HTTP 500 `{ "error": "..." }`.
- Unity process failures are detected via log pattern matching, not exit codes.
- The temp extract directory is always deleted in `GenerationEngine`'s `finally` block even if an exception propagates.

---

## Adding a New Built-in Template

1. Add a new entry to the `Builtins` dictionary in `TemplateResolver` (Section 4).
2. The key is the template name callers will use in `scene.json` (e.g. `"lighting/spot"`).
3. Declare `Components` (array of `ComponentConfig`) and `PropMappings` (friendly name → `"Type.propName"`).
4. No registration or factory wiring needed — `TemplateResolver.FindTemplate` checks `Builtins` automatically.

## Adding a New API Endpoint

1. Add a new `case` to the `switch (req.HttpMethod, path)` in `HandleAsync` (Section 10).
2. Implement a `private async Task Handle<Name>Async(HttpListenerRequest, HttpListenerResponse)` method.
3. Update the `RootInfo()` endpoint list and the `OpenApiSpec()` JSON.
4. If the endpoint runs the pipeline, acquire `_gate.TryAcquire()` and return 503 if `null`.

---
---

# UnitySceneGen — API Caller Guide

> **Audience:** Developers integrating with or building clients against the UnitySceneGen HTTP API. No knowledge of the internal implementation is required.

---

## Base URL and Port

The server always listens on port **46001** and binds to all interfaces (`0.0.0.0`), so it is reachable from remote machines:

```
http://<host>:46001
```

CORS headers (`Access-Control-Allow-Origin: *`) are set on every response, so browser-based clients work without a proxy.

---

## Quick-Start Workflow

```
1. GET  /schema            → fetch available components and templates
2.      Build your scene.zip (see "ZIP Input Format" below)
3. POST /validate          → verify scene.zip without running Unity (~1 s)
4. POST /generate          → run the full pipeline, stream progress via SSE
5. GET  /status            → poll current step if you need a simpler status check
6.      Receive the output ZIP in the SSE "result" event → decode + save
```

---

## Endpoint Reference

### GET /

Returns a JSON summary of the service, version, default Unity path, and a full endpoint list. Useful as a health-check.

**Response 200**
```json
{
  "service": "UnitySceneGen API",
  "version": "2.0.0",
  "port": 46001,
  "defaultUnityExePath": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "endpoints": [ "..." ]
}
```

---

### GET /schema

Returns the full catalog of every supported Unity component and every built-in template, including their accepted props, types, defaults, and any usage notes. **Read this endpoint once before building your first `scene.json`** — it is the authoritative reference for what you can put in the `components` array.

**Response 200** — a large JSON object with two top-level keys:

```json
{
  "templates": {
    "usage": [ "..." ],
    "builtins": {
      "physics/rigidbody": {
        "description": "...",
        "propMappings": { "mass": "UnityEngine.Rigidbody.mass", ... },
        "componentTypes": [ "UnityEngine.Rigidbody", "UnityEngine.BoxCollider" ]
      }
    }
  },
  "components": {
    "UnityEngine.Transform": {
      "notes": [ "..." ],
      "props": {
        "localPosition": { "type": "Vector3", "default": "[0, 0, 0]" },
        ...
      }
    },
    ...
  }
}
```

---

### GET /status

Returns the current pipeline state. Poll this if you are not consuming the SSE stream from `/generate`.

**Response 200**
```json
{
  "running": true,
  "step": "5/6 — Unity Pass 1 (compile)",
  "error": "",
  "log": [ "[ZipLoader] ...", "=== Step 1/6 ..." ]
}
```

| Field | Type | Description |
|---|---|---|
| `running` | bool | `true` while a job is in progress |
| `step` | string | Human-readable current stage |
| `error` | string | Non-empty only after a failure |
| `log` | string[] | All log lines accumulated since the job started |

---

### GET /swagger

Opens an interactive Swagger UI in the browser. Lets you try all endpoints without writing any code.

---

### GET /openapi.json

Returns the OpenAPI 3.0 specification as JSON. Import it into Postman, Insomnia, or any OpenAPI toolchain.

---

### GET /apidocs

Returns the contents of `API.md` (if present alongside the executable) as `text/markdown`. Returns 404 if the file is not found.

---

### GET /readmedocs

Returns the contents of `README.md` (if present alongside the executable) as `text/markdown`. Returns 404 if the file is not found.

---

### POST /validate

Validates a `scene.zip` through the full load and resolution pipeline **without** invoking Unity. This is cheap (under one second) and should be your first check before calling `/generate`.

**Request** — `Content-Type: application/json`
```json
{
  "sceneZipBase64": "<base64-encoded scene.zip bytes>"
}
```

**Response 200** — valid
```json
{
  "valid": true,
  "errors": [],
  "warnings": [ "Scene 'Main' has no root GameObjects — the scene will be empty." ]
}
```

**Response 422** — invalid
```json
{
  "valid": false,
  "errors": [ "'player.bullet' has child 'bullet.hit' but no GameObject with that id exists." ],
  "warnings": []
}
```

**Response 400** — malformed request body or invalid base64.

> Errors block generation. Warnings are informational and will not block it.

---

### POST /generate

Runs the full 6-step generation pipeline and streams progress in real time using **Server-Sent Events (SSE)**. The connection stays open until generation finishes, fails, or the client disconnects.

**Request** — `Content-Type: application/json`
```json
{
  "sceneZipBase64": "<base64-encoded scene.zip>",
  "unityExePath":   "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "outputDir":      "C:\\Builds\\MyProject",
  "force":          false
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `sceneZipBase64` | **yes** | — | Base64-encoded bytes of your `scene.zip` |
| `unityExePath` | no | `C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe` | Full path to the Unity executable on the server |
| `outputDir` | no | A new temp directory | Server-side path where the Unity project folder is written |
| `force` | no | `false` | If `true`, deletes any existing project folder at `outputDir/<projectName>` before regenerating |

**Response** — `Content-Type: text/event-stream`

The response is an SSE stream. Three event types are emitted:

**Log lines** (no `event:` prefix — plain `data:` frames):
```
data: [ZipLoader] Extracting 14 KB to C:\Temp\UnitySceneGen_abc123\n\n
data: === Step 1/6: Loading ZIP ===\n\n
```

**Success** (`event: result`):
```
event: result
data: {"success":true,"projectName":"MyGame","sizeKb":4821,"warnings":[],"zipBase64":"<base64>"}
```

| Field | Type | Description |
|---|---|---|
| `success` | bool | Always `true` in this event |
| `projectName` | string | Name of the generated Unity project folder |
| `sizeKb` | int | Approximate size of the output ZIP in kilobytes |
| `warnings` | string[] | Non-fatal warnings collected during generation |
| `zipBase64` | string | Base64-encoded output ZIP — decode and save as `<projectName>_output.zip` |

**Failure** (`event: error`):
```
event: error
data: {"error":"Compilation failed: error CS0246 ...","warnings":[]}
```

**Response 503** — another job is already running. Poll `GET /status` until `running` is `false`, then retry.

**Response 400** — `sceneZipBase64` missing, not valid base64, or Unity executable not found on the server.

---

### POST /generate/upload

Identical pipeline to `POST /generate`, but accepts the ZIP as a **multipart/form-data** file upload rather than base64 JSON. Use this from web browsers or any client that prefers binary uploads.

**Request** — `Content-Type: multipart/form-data`

| Field | Required | Description |
|---|---|---|
| `file` | **yes** | The `scene.zip` file (binary) |
| `unityExePath` | no | Same as `/generate` |
| `outputDir` | no | Same as `/generate` |
| `force` | no | Same as `/generate` (send as string `"true"` or `"false"`) |

**Response** — identical SSE stream format as `POST /generate`.

**Example (curl):**
```bash
curl -N -X POST http://localhost:46001/generate/upload \
  -F "file=@scene.zip" \
  -F "force=false"
```

---

### POST /build

Accepts an **already-generated Unity project ZIP** (i.e. the output from `/generate`) and runs a WebGL build pass on it. Optionally uploads the resulting build to Google Cloud Storage.

This endpoint is synchronous — it waits for both Unity passes to complete and then returns a single JSON response (no SSE streaming).

**Request** — `Content-Type: application/json`
```json
{
  "projectZipBase64": "<base64-encoded Unity project zip>",
  "projectName":      "MyGame",
  "unityExePath":     "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "gcsBucket":        "my-gcs-bucket",
  "gcsKeyJson":       "<base64-encoded GCS service account key JSON>"
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `projectZipBase64` | **yes** | — | Base64-encoded ZIP that contains a folder named exactly `projectName` |
| `projectName` | **yes** | — | The name of the top-level folder inside the ZIP |
| `unityExePath` | no | Default path | Full path to Unity on the server |
| `gcsBucket` | no | `aqe-unity-builds` | GCS bucket to upload to. Omit or set to empty string to skip upload |
| `gcsKeyJson` | no | — | Base64-encoded GCS service account key JSON. Required for upload; omit to skip |

**Response 200** — success
```json
{
  "success": true,
  "url": "https://storage.googleapis.com/my-gcs-bucket/MyGame/index.html",
  "buildPath": "C:\\Temp\\UnityBuild_abc123\\MyGame",
  "log": [ "[Build] Extracting source ZIP...", "..." ],
  "warnings": []
}
```

`url` is only present when a GCS bucket and key were provided. If GCS upload was skipped, `url` is `null`.

**Response 422** — Unity pass failed. Body includes `error` and `log` array.

**Response 503** — another job is running.

---

## ZIP Input Format

The ZIP you send to `/validate` or `/generate` must follow this layout:

```
scene.zip
├── scene.json           ← required (at root OR one folder deep)
├── scripts/             ← optional; .cs files are auto-discovered recursively
│   └── PlayerController.cs
└── templates/           ← optional; custom template definitions
    └── my_template.json
```

`scene.json` is the only required file. Scripts are **never** declared inside `scene.json` — just drop `.cs` files in `scripts/` and they are picked up automatically.

---

## scene.json Reference

`scene.json` is the declarative description of the Unity project you want to generate. It has four top-level keys.

### Top-level Structure

```json
{
  "project": { ... },
  "settings": { ... },
  "scenes": [ ... ],
  "gameObjects": [ ... ]
}
```

---

### `project` block

```json
"project": {
  "name": "MyGame",
  "unityVersion": "2022.3.20f1",
  "packages": []
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `name` | **yes** | `"MyProject"` | Unity project name. Becomes the output folder name. Must be a valid folder name. |
| `unityVersion` | no | `"2022.3.20f1"` | Written to `ProjectSettings/ProjectVersion.txt` |
| `packages` | no | `[]` | Reserved for future use; currently unused |

---

### `settings` block

Declare any **non-built-in** tags and layers here. Using a tag or layer on a GameObject without declaring it first is a validation error.

```json
"settings": {
  "tags":   ["Enemy", "Pickup", "Terrain"],
  "layers": ["Background", "Foreground"]
}
```

**Built-in tags** (no declaration needed): `Untagged`, `Respawn`, `Finish`, `EditorOnly`, `MainCamera`, `Player`, `GameController`

**Built-in layers** (no declaration needed): `Default`, `TransparentFX`, `Ignore Raycast`, `Water`, `UI`

---

### `scenes` array

Each entry creates one Unity scene file.

```json
"scenes": [
  {
    "name": "Main",
    "path": "Assets/Scenes/Main.unity",
    "roots": ["camera", "light", "player"]
  }
]
```

| Field | Required | Description |
|---|---|---|
| `name` | **yes** | Display name of the scene |
| `path` | **yes** | Asset path where the `.unity` file will be created. Convention: `Assets/Scenes/<Name>.unity` |
| `roots` | no | IDs of top-level GameObjects in this scene. Objects not listed as a root or child of a root will not appear in the scene. |

---

### `gameObjects` array

Each entry is a Unity `GameObject`. Objects are defined globally and then wired into scenes via `roots` and `children`.

```json
"gameObjects": [
  {
    "id":       "player",
    "name":     "Player",
    "active":   true,
    "tag":      "Player",
    "layer":    "Default",
    "children": ["player.weapon", "player.camera"],
    "components": [
      {
        "type": "UnityEngine.Transform",
        "props": {
          "localPosition": [0, 1, 0]
        }
      },
      {
        "type": "PlayerController"
      }
    ]
  }
]
```

| Field | Required | Default | Description |
|---|---|---|---|
| `id` | **yes** | — | Unique string identifier. Used in `roots`, `children`, and `ref:` prop values |
| `name` | **yes** | `"GameObject"` | The name shown in the Unity Hierarchy panel |
| `active` | no | `true` | Whether the GameObject is active on start |
| `tag` | no | `"Untagged"` | Must be a built-in tag or declared in `settings.tags` |
| `layer` | no | `"Default"` | Must be a built-in layer or declared in `settings.layers` |
| `children` | no | `[]` | IDs of child GameObjects. Cyclic references are a validation error. |
| `components` | no | `[]` | Component list (see below). Can be omitted when using a `template`. |
| `template` | no | — | Shorthand to expand a built-in or file template (see Templates) |
| `templateProps` | no | — | Friendly property overrides applied during template expansion |

**ID naming convention:** Children should prefix their ID with `parentId.` — e.g. `"player.weapon"` as a child of `"player"`. This is a convention only; violations produce a warning, not an error.

---

### Component format

Each entry in a `components` array has a `type` and an optional `props` dictionary.

```json
{
  "type": "UnityEngine.Rigidbody",
  "props": {
    "mass": 5.0,
    "useGravity": true,
    "isKinematic": false
  }
}
```

`type` is the fully-qualified Unity class name (e.g. `UnityEngine.Rigidbody`, `TMPro.TextMeshProUGUI`). For your own `MonoBehaviour` scripts, use just the class name with no namespace (e.g. `"PlayerController"`).

---

### Prop value types

| Prop type | JSON representation | Example |
|---|---|---|
| `bool` | JSON boolean | `true` |
| `int` / `float` | JSON number | `5`, `1.5` |
| `string` / `enum` | JSON string | `"Skybox"`, `"Normal"` |
| `Vector2` | 2-element number array | `[1920, 1080]` |
| `Vector3` | 3-element number array | `[0, 1, 0]` |
| `Vector4` | 4-element number array | `[0, 0, 1, 1]` |
| `Color` | 9-character hex string `#RRGGBBAA` | `"#FF0000FF"` |
| GameObject reference | String prefixed with `ref:` | `"ref:player"` |

**Color format:** Always 8 hex digits after `#` — RRGGBBAA. Example: fully-opaque red is `"#FF0000FF"`. Any other format is a validation error.

**`ref:` references:** Used to point one component prop at another GameObject. The ID after `ref:` must exist in `gameObjects`. Example:

```json
{
  "type": "UnityEngine.Camera",
  "props": {
    "targetTexture": "ref:renderTarget"
  }
}
```

**Arrays:** Must contain exactly 2, 3, or 4 numbers. All elements must be numeric.

---

### Special component rules

**`UnityEngine.Transform`** — Every GameObject already has a Transform. You only need to include it in `components` if you want to set `localPosition`, `localEulerAngles`, or `localScale`. Do **not** add it to position/rotate/scale a UI element — use `UnityEngine.RectTransform` instead.

**`UnityEngine.Canvas`** — Must be the **first** entry in the `components` array. It promotes the implicit `Transform` to a `RectTransform`.

**`UnityEngine.UI.Button`** — Requires an `Image` on the same GameObject. Wire `onClick` handlers via a MonoBehaviour script in `scripts/`.

**`UnityEngine.EventSystems.EventSystem`** — One per scene. Always pair it with `UnityEngine.EventSystems.StandaloneInputModule` on the same GameObject.

---

### Using templates

Templates are a shorthand that expand into a pre-defined `components` list. Instead of spelling out every component, write:

```json
{
  "id": "ball",
  "name": "Ball",
  "template": "physics/rigidbody",
  "templateProps": {
    "mass": 2.5,
    "useGravity": true
  }
}
```

`templateProps` maps to the template's declared `propMappings` (see `GET /schema` for the full list per template). Any `components` you also specify are **merged on top** of the template expansion — your explicit values always win.

**Built-in templates:**

| Key | Creates |
|---|---|
| `basic/transform` | `Transform` only |
| `basic/mesh` | `MeshRenderer` + `MeshFilter` + `Transform` |
| `ui/canvas` | `Canvas` + `CanvasScaler` + `GraphicRaycaster` |
| `ui/button` | `RectTransform` + `Image` + `Button` + `TextMeshProUGUI` |
| `ui/label` | `RectTransform` + `TextMeshProUGUI` |
| `physics/rigidbody` | `Rigidbody` + `BoxCollider` |
| `physics/trigger` | `BoxCollider` (isTrigger = true) |
| `audio/source` | `AudioSource` |

**Custom file templates** — place a `.json` file in `templates/` inside your ZIP:

```json
{
  "name": "my_template",
  "description": "My custom template",
  "components": [
    { "type": "UnityEngine.MeshRenderer" },
    { "type": "MyCustomScript" }
  ],
  "propMappings": {
    "scriptSpeed": "MyCustomScript.speed"
  }
}
```

---

## Scripts

Drop `.cs` files into the `scripts/` folder in your ZIP. They are copied to `Assets/Scripts/` in the generated project.

Rules:
- File names (minus `.cs`) must be valid C# identifiers: letters, digits, and underscores only, starting with a letter or underscore.
- Two files with the same name (case-insensitive) is a validation error.
- To attach a script as a component, use its class name (no namespace) as the component `type`: `"type": "PlayerController"`.

---

## Complete scene.json Example

```json
{
  "project": {
    "name": "DemoGame",
    "unityVersion": "2022.3.20f1"
  },
  "settings": {
    "tags": ["Enemy"],
    "layers": []
  },
  "scenes": [
    {
      "name": "Main",
      "path": "Assets/Scenes/Main.unity",
      "roots": ["camera", "light", "player", "ui.canvas"]
    }
  ],
  "gameObjects": [
    {
      "id": "camera",
      "name": "Main Camera",
      "tag": "MainCamera",
      "components": [
        {
          "type": "UnityEngine.Transform",
          "props": { "localPosition": [0, 5, -10] }
        },
        { "type": "UnityEngine.Camera" },
        { "type": "UnityEngine.AudioListener" }
      ]
    },
    {
      "id": "light",
      "name": "Directional Light",
      "components": [
        {
          "type": "UnityEngine.Transform",
          "props": { "localEulerAngles": [50, -30, 0] }
        },
        {
          "type": "UnityEngine.Light",
          "props": { "type": "Directional", "intensity": 1.2, "shadows": "Soft" }
        }
      ]
    },
    {
      "id": "player",
      "name": "Player",
      "tag": "Player",
      "template": "physics/rigidbody",
      "templateProps": { "mass": 1.0, "useGravity": true },
      "components": [
        {
          "type": "UnityEngine.Transform",
          "props": { "localPosition": [0, 0.5, 0] }
        },
        { "type": "PlayerController" }
      ]
    },
    {
      "id": "ui.canvas",
      "name": "UI Canvas",
      "template": "ui/canvas",
      "children": ["ui.canvas.healthLabel"]
    },
    {
      "id": "ui.canvas.healthLabel",
      "name": "Health Label",
      "template": "ui/label",
      "templateProps": { "label": "HP: 100" }
    }
  ]
}
```

---

## Consuming the SSE Stream

Both `/generate` and `/generate/upload` return a Server-Sent Events stream. Keep the connection open until you receive `event: result` or `event: error`.

**JavaScript (browser) example:**
```javascript
const es = new EventSource("http://localhost:46001/generate");
// Note: EventSource is GET-only. For POST, use fetch with a ReadableStream instead:

const response = await fetch("http://localhost:46001/generate", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ sceneZipBase64: base64Zip })
});

const reader = response.body.getReader();
const decoder = new TextDecoder();
let buffer = "";

while (true) {
  const { done, value } = await reader.read();
  if (done) break;
  buffer += decoder.decode(value, { stream: true });

  const lines = buffer.split("\n");
  buffer = lines.pop(); // keep incomplete last line

  for (const line of lines) {
    if (line.startsWith("event: result")) {
      // next "data:" line has the result
    } else if (line.startsWith("data: ")) {
      const raw = line.slice(6).replace(/\\n/g, "\n");
      try {
        const payload = JSON.parse(raw);
        if (payload.zipBase64) {
          // decode and save the output ZIP
          const bytes = atob(payload.zipBase64);
          // ... write to file
        }
      } catch {
        console.log("[log]", raw); // plain log line
      }
    }
  }
}
```

**Python example:**
```python
import requests, base64, json

with open("scene.zip", "rb") as f:
    zip_b64 = base64.b64encode(f.read()).decode()

with requests.post(
    "http://localhost:46001/generate",
    json={"sceneZipBase64": zip_b64},
    stream=True
) as resp:
    event_type = None
    for raw in resp.iter_lines(decode_unicode=True):
        if raw.startswith("event:"):
            event_type = raw[6:].strip()
        elif raw.startswith("data:"):
            data = raw[5:].strip().replace("\\n", "\n")
            if event_type == "result":
                payload = json.loads(data)
                out = base64.b64decode(payload["zipBase64"])
                with open(f"{payload['projectName']}_output.zip", "wb") as f:
                    f.write(out)
                print(f"Done — {payload['sizeKb']} KB")
                break
            elif event_type == "error":
                payload = json.loads(data)
                print("FAILED:", payload["error"])
                break
            else:
                print(data)  # log line
            event_type = None
```

---

## HTTP Status Codes

| Code | Meaning |
|---|---|
| 200 | Success (or SSE stream opened) |
| 400 | Bad request — missing/invalid field, bad base64, Unity exe not found |
| 422 | Unprocessable — validation failed or Unity build failed |
| 503 | Server busy — another generation job is already running |
| 500 | Unexpected internal error |

---

## Concurrency Limit

The server processes **one job at a time**. A second `POST /generate` (or `/build`, `/generate/upload`) while a job is running immediately returns HTTP 503:

```json
{
  "error": "Application is currently busy. Cannot process request.",
  "hint": "Poll GET /status until 'running' is false, then retry."
}
```

Poll `GET /status` and wait for `"running": false` before retrying.

---

## Timing Expectations

| Stage | Typical Duration |
|---|---|
| `/validate` (no Unity) | < 1 second |
| Unity Pass 1 — import + compile | 3–12 minutes (first run); faster on re-run |
| Unity Pass 2 — scene build | 1–8 minutes |
| **Total end-to-end** | **5–20 minutes** |

Pass 1 has a hard timeout of **12 minutes** and a hang-detection kill after **120 seconds of no log output**. Pass 2 has a **8-minute** hard timeout. Design your HTTP client accordingly — do not set a short socket timeout on the SSE connection.

---

## Validation Error Reference

The table below lists the most common errors returned by `POST /validate` (and by `/generate` at step 3).

| Error message pattern | Cause | Fix |
|---|---|---|
| `'project.name' is required` | `project.name` is empty or missing | Set a non-empty project name |
| `'scenes' must contain at least one entry` | Empty `scenes` array | Add at least one scene |
| `A scene entry is missing 'name'` | Scene `name` field is blank | Provide a `name` for every scene |
| `Scene 'X': missing 'path'` | Scene `path` field is blank | Add `"path": "Assets/Scenes/X.unity"` |
| `A GameObject (name: 'X') has no 'id'` | `id` field is blank | Give every GameObject a unique `id` |
| `Duplicate GameObject id: 'X'` | Two objects share an ID | Use distinct IDs |
| `'X' has child 'Y' but no GameObject with that id exists` | Child ID is missing from `gameObjects` | Add the child object or remove the reference |
| `Scene 'X' lists root 'Y' but it doesn't exist` | Root ID is not in `gameObjects` | Add the object or fix the root ID |
| `Cycle in GameObject hierarchy: A → B → A` | `children` reference forms a loop | Remove the circular child reference |
| `'X' uses tag 'Y' not declared in settings.tags` | Custom tag used without declaration | Add `"Y"` to `settings.tags` |
| `'X' uses layer 'Y' not declared in settings.layers` | Custom layer used without declaration | Add `"Y"` to `settings.layers` |
| `'CompType' on 'X': 'color' is not a valid color` | Color value not in `#RRGGBBAA` format | Use 8 hex digits: `"#FF0000FF"` |
| `'CompType' on 'X': 'prop' references 'Y' which doesn't exist` | `ref:Y` points to a missing ID | Add the target object or fix the ID |
| `'CompType' on 'X': 'prop' array has N elements` | Vector array length is not 2, 3, or 4 | Use `[x, y]`, `[x, y, z]`, or `[x, y, z, w]` |
| `Script name 'X' is not a valid C# identifier` | Script filename contains invalid characters | Rename the `.cs` file |
| `'X' still has unresolved template 'Y'` | Template name was not found | Use a built-in key or add the file template to `templates/` |