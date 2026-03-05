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

#### Concurrency

Unity's license system only allows **one headless instance at a time**.
The server enforces this with a semaphore — a second `POST /generate` arriving
while one is already running receives `503 Service Unavailable` immediately
with `{ "status": "busy" }`. Retry when the current job completes.

#### Client timeout

`POST /generate` holds the HTTP connection open for the full Unity run
(typically 5–20 minutes depending on project size and machine speed).
Set your client timeout accordingly:

```cmd
curl -X POST http://localhost:5782/generate ^
  -H "Content-Type: application/json" ^
  -d @MyScene.json ^
  --output MyProject.zip ^
  --max-time 1800
```

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

### `GET /`

Returns a JSON info object listing the server version, default Unity path,
and all available endpoints.

### `GET /swagger`

Serves the Swagger UI HTML page. Open in a browser to get a fully interactive
API explorer with the request example pre-filled and a Download link for the
returned ZIP.

### `GET /openapi.json`

Returns the raw OpenAPI 3.0 specification. Import into Postman, Insomnia,
or any other API client.

### `POST /generate`

Runs the full generation pipeline and returns the project as a ZIP archive.

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

| Field | Required | Description |
|-------|----------|-------------|
| `config` | ✓ | Full UnitySceneGen scene config object (same schema as the `.json` file) |
| `unityExePath` |  | Path to Unity.exe on the server machine. Omit to use the default path |
| `force` |  | `true` to delete and recreate the project. Default `false` |

**Responses:**

| Status | Body | When |
|--------|------|------|
| `200` | `application/zip` — the generated project folder | Success |
| `400` | JSON error | Missing `config`, bad JSON, or Unity.exe not found |
| `422` | JSON error + `warnings` + `log` | Config valid but Unity pipeline failed |
| `503` | JSON `{ "status": "busy" }` | Another generation is already running |
| `500` | JSON error | Unexpected server exception |

**Response headers on 200:**

| Header | Description |
|--------|-------------|
| `Content-Disposition` | `attachment; filename="<projectName>.zip"` |
| `X-Warnings` | Pipe-separated builder warnings, empty string if none |

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

### GameObject fields

| Field | Default | Description |
|-------|---------|-------------|
| `id` | — | Unique string identifier used for parent/child references |
| `name` | `"GameObject"` | Display name in the Hierarchy |
| `active` | `true` | Sets `GameObject.SetActive()` |
| `tag` | `"Untagged"` | Must be listed in `settings.tags` or be a Unity built-in tag |
| `layer` | `"Default"` | Must be listed in `settings.layers` or be a Unity built-in layer |
| `children` | `[]` | List of child GameObject `id` values |
| `components` | `[]` | Components to add — each has `type` and optional `props` |

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
A generation job is already running. Wait for it to complete (check the server console output) then retry.

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