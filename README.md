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
9. Tags + layers declared if non-built-in
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