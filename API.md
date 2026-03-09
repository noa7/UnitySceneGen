# UnitySceneGen API Reference

> **Audience:** Callers of the HTTP API — including LLMs, automation scripts, and CI pipelines.
> **Base URL:** `http://localhost:46001` (fixed port; cannot be overridden at runtime)
> **Authentication:** None. CORS is open (`*`).

---

## Quick-Start Workflow

```
1.  GET  /schema          → fetch the component + template catalog (once per session)
2.  Build your scene.zip  → scene.json + optional scripts/ folder
3.  POST /validate        → fast structural check (< 1 s, no Unity required)
4.  POST /generate        → full pipeline with real-time SSE log stream (5–20 min)
       OR
    POST /generate/upload → same pipeline via multipart file upload + SSE
5.  Receive the result event from the SSE stream → extract zipBase64
    Poll GET /status at any time to check progress or review accumulated logs
```

---

## Endpoints

### `GET /schema`

Returns the complete machine-readable catalog of supported Unity component types, built-in templates, prop formats, ID conventions, and the `scene.zip` input contract. **Fetch this once per session before building `scene.json`** — it contains everything an LLM needs to generate valid configs.

**Response `200`** — `application/json`

Top-level keys:

| Key | Description |
|---|---|
| `version` | Schema version string (e.g. `"2.0"`) |
| `usage` | Recommended workflow steps and summary |
| `inputContract` | Full `scene.zip` structure, rules, field definitions, and a worked example |
| `propFormats` | How to encode Color, Vector2/3/4, ref, enum, float, int, bool, string values |
| `idConvention` | Dot-notation ID naming rule with examples |
| `builtinTags` | Unity built-in tag names (no declaration needed in `settings.tags`) |
| `builtinLayers` | Unity built-in layer names (no declaration needed in `settings.layers`) |
| `templates` | `{ usage: string[], builtins: { [name]: { description, propMappings, componentTypes } } }` |
| `components` | `{ [UnityEngine.TypeName]: { notes: string[], props: { [propName]: { type, default, range?, values?, notes? } } } }` |

> The `templates.builtins` and `components` objects are the primary reference for building `scene.json`. Always prefer `/schema` over hardcoded assumptions — the catalog is authoritative.

---

### `GET /status`

Returns a snapshot of the currently running (or last completed) job. Safe to call at any time, including while a job is running. Use this to poll progress while `/generate` or `/generate/upload` is streaming.

**Response `200`** — `application/json`

```json
{
  "running": true,
  "step": "5/6 — Unity Pass 1 (compile)",
  "error": "",
  "log": [
    "=== Step 1/6: Loading scene zip ===",
    "[ZipLoader] Extracting 14 KB to C:\\Temp\\UnitySceneGen_abc123",
    "=== Step 2/6: Resolving templates ===",
    "..."
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `running` | bool | `true` while a job holds the processing slot |
| `step` | string | Current human-readable step description |
| `error` | string | Non-empty if the last job failed |
| `log` | string[] | All log lines accumulated since the job started |

> `/status` accumulates all log lines for the lifetime of the job. Use the SSE stream on `/generate` or `/generate/upload` for real-time line-by-line delivery.

---

### `POST /validate`

Validates a `scene.zip` without invoking Unity. Runs steps 1–3 of the pipeline (extract → resolve templates → validate config). Completes in under one second. Call this before `/generate` to catch structural errors cheaply.

**Request body** — `application/json`

```json
{
  "sceneZipBase64": "<base64-encoded scene.zip>"
}
```

**Response `200`** — valid:
```json
{
  "valid": true,
  "errors": [],
  "warnings": ["Scene 'Main' has no root GameObjects — the scene will be empty."]
}
```

**Response `422`** — invalid:
```json
{
  "valid": false,
  "errors": [
    "Duplicate GameObject id: 'player'.",
    "'player.weapon' has child 'player.weapon.barrel' but no GameObject with that id exists."
  ],
  "warnings": []
}
```

**Response `400`** — bad request (missing field, bad base64, or malformed JSON):
```json
{ "error": "sceneZipBase64 is required." }
```

---

### `POST /generate`  ← **Primary generation endpoint. Streams logs via SSE.**

Runs the full 6-step pipeline. The response is a **Server-Sent Events (SSE) stream** that delivers every log line in real time, then terminates with either a `result` or `error` event.

**This endpoint does NOT return an HTTP error status on pipeline failure.** The HTTP response is always `200`. Success/failure is communicated through SSE events.

#### Request

**Content-Type:** `application/json`

```json
{
  "sceneZipBase64": "<base64-encoded scene.zip>",
  "unityExePath":   "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.3f1\\Editor\\Unity.exe",
  "outputDir":      "C:\\MyBuilds\\output",
  "force":          false
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `sceneZipBase64` | ✅ | — | Base64-encoded `scene.zip` |
| `unityExePath` | ❌ | `C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe` | Full path to Unity executable |
| `outputDir` | ❌ | `%TEMP%\UnitySceneGen_out_<GUID>` | Where to write the Unity project folder |
| `force` | ❌ | `false` | If `true`, delete existing project folder before creation |

**Pre-flight errors** (`400` or `503`) are returned as plain JSON before the SSE stream opens:
```json
{ "error": "Unity executable not found: C:\\...", "defaultPath": "C:\\..." }
{ "error": "Application is currently busy.", "hint": "Poll GET /status until 'running' is false, then retry." }
```

#### SSE Response Stream

**Content-Type:** `text/event-stream`

The stream contains three types of events:

**1. Log lines** (emitted continuously throughout the job)
```
data: === Step 1/6: Loading scene zip ===

data: [ZipLoader] Extracting 14 KB to C:\Temp\UnitySceneGen_abc123

data: [ZipLoader] Found scene.json: C:\Temp\UnitySceneGen_abc123\scene.json

data:   [WARN] SCENE 'MAIN' HAS NO ROOT GAMEOBJECTS — THE SCENE WILL BE EMPTY.

data: === Step 5/6: Unity Pass 1 — Package import & compile ===

data:   [Unity] PID 9823 started.

data:   [Unity] Refreshing asset database...

data: [ERROR] UNITY LOG CONTAINS FAILURE PATTERN: 'ERROR CS'
```

**2. Success result event** (emitted once, at the very end)
```
event: result
data: {"success":true,"projectName":"MyProject","sizeKb":4210,"warnings":[],"zipBase64":"<base64>"}
```

| Field | Type | Description |
|---|---|---|
| `success` | bool | Always `true` in a `result` event |
| `projectName` | string | Name of the generated Unity project |
| `sizeKb` | int | Approximate size of the output ZIP in kilobytes |
| `warnings` | string[] | Non-fatal warnings from the pipeline |
| `zipBase64` | string | Base64-encoded ZIP of the complete Unity project |

**3. Error event** (emitted once, at the very end, if the job failed)
```
event: error
data: {"error":"Unity Pass 1 failed: Unity log contains failure: 'error CS'","warnings":[]}
```

| Field | Type | Description |
|---|---|---|
| `error` | string | Human-readable failure reason |
| `warnings` | string[] | Any non-fatal warnings collected before failure |

#### Reading the SSE Stream — Python Example

```python
import requests, json, base64

with open("scene.zip", "rb") as f:
    zip_b64 = base64.b64encode(f.read()).decode()

payload = {"sceneZipBase64": zip_b64}

with requests.post("http://localhost:46001/generate",
                   json=payload, stream=True) as resp:
    resp.raise_for_status()

    event_type = "message"
    for raw_line in resp.iter_lines(decode_unicode=True):
        if raw_line.startswith("event:"):
            event_type = raw_line[len("event:"):].strip()
        elif raw_line.startswith("data:"):
            data = raw_line[len("data:"):].strip()

            if event_type == "result":
                result = json.loads(data)
                zip_bytes = base64.b64decode(result["zipBase64"])
                with open("output.zip", "wb") as out:
                    out.write(zip_bytes)
                print("Done:", result["projectName"], result["sizeKb"], "KB")
                break

            elif event_type == "error":
                err = json.loads(data)
                raise RuntimeError(f"Generation failed: {err['error']}")

            else:
                # Plain log line
                print(data)

        elif raw_line == "":
            event_type = "message"   # reset after blank separator
```

#### Reading the SSE Stream — Node.js Example

```javascript
const fetch = require('node-fetch');
const fs = require('fs');

const zipB64 = fs.readFileSync('scene.zip').toString('base64');

const res = await fetch('http://localhost:46001/generate', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ sceneZipBase64: zipB64 }),
});

let eventType = 'message';
let buffer = '';

for await (const chunk of res.body) {
  buffer += chunk.toString();
  const lines = buffer.split('\n');
  buffer = lines.pop(); // keep incomplete last line

  for (const line of lines) {
    if (line.startsWith('event:')) {
      eventType = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      const data = line.slice(5).trim();
      if (eventType === 'result') {
        const result = JSON.parse(data);
        fs.writeFileSync('output.zip', Buffer.from(result.zipBase64, 'base64'));
        console.log('Done:', result.projectName, result.sizeKb, 'KB');
      } else if (eventType === 'error') {
        const err = JSON.parse(data);
        throw new Error('Generation failed: ' + err.error);
      } else {
        console.log(data); // plain log line
      }
    } else if (line === '') {
      eventType = 'message';
    }
  }
}
```

---

### `POST /generate/upload`

Same full pipeline as `/generate`, but accepts the ZIP as a **multipart/form-data** file upload instead of base64 JSON. Also streams SSE logs. Useful for direct browser uploads or tools that prefer not to base64-encode.

**Content-Type:** `multipart/form-data`

| Field name | Type | Required | Description |
|---|---|---|---|
| `file` | binary | ✅ | The `scene.zip` file |
| `unityExePath` | string | ❌ | Path to Unity executable |
| `outputDir` | string | ❌ | Output directory |
| `force` | string (`"true"`/`"false"`) | ❌ | Force-delete existing project |

**SSE response format:** Identical to `/generate` — same `data:` log lines, `event: result`, `event: error`.

---

## Concurrency and Busy Handling

Only one job can run at a time. Both the GUI and the API share a single processing slot.

If you call `/generate` or `/generate/upload` while another job is running, you will receive:

**HTTP 503:**
```json
{
  "error": "Application is currently busy. Cannot process request.",
  "hint": "Poll GET /status until 'running' is false, then retry."
}
```

**Recommended retry pattern:**

```python
import time, requests

def wait_until_free(base_url, poll_interval=2.0, timeout=1800):
    deadline = time.time() + timeout
    while time.time() < deadline:
        status = requests.get(f"{base_url}/status").json()
        if not status["running"]:
            return
        time.sleep(poll_interval)
    raise TimeoutError("Server stayed busy for too long")

wait_until_free("http://localhost:46001")
# now safe to call /generate
```

---

## `scene.zip` Format

This is the primary input to `/validate` and `/generate`.

```
scene.zip
├── scene.json                ← REQUIRED. Root config file.
├── scripts/                  ← OPTIONAL. All .cs files are auto-included.
│   ├── PlayerController.cs
│   └── EnemyAI.cs
└── templates/                ← OPTIONAL. Custom template definitions.
    └── my_prefab.json
```

`scene.json` can be at the root of the ZIP or inside one subfolder (e.g. `MyProject/scene.json`). Any deeper nesting is not supported.

### `scene.json` schema

```json
{
  "project": {
    "name": "MyGame",
    "unityVersion": "2022.3.20f1",
    "packages": ["com.unity.cinemachine@2.9.7"]
  },
  "settings": {
    "tags": ["Enemy", "Pickup"],
    "layers": ["Enemies", "UI"]
  },
  "scenes": [
    {
      "name": "Main",
      "path": "Assets/Scenes/Main.unity",
      "roots": ["env", "player", "ui"]
    }
  ],
  "gameObjects": [
    {
      "id": "player",
      "name": "Player",
      "active": true,
      "tag": "Player",
      "layer": "Default",
      "template": "basic/mesh",
      "templateProps": { "meshType": "Capsule" },
      "children": ["player.weapon"],
      "components": [
        {
          "type": "PlayerController",
          "props": { "speed": 5.0, "jumpForce": 10.0 }
        }
      ]
    }
  ]
}
```

### `scene.json` rules

- `project`, `settings` are optional blocks. Defaults are used if omitted.
- `scenes` must have at least one entry. Each scene needs `name`, `path`, and a `roots` list.
- Every `gameObjects` entry must have a unique `id`. IDs should use dot-notation for hierarchy: `"player.weapon.barrel"` is a child of `"player.weapon"`.
- `template` and `components` can coexist. Explicit `components` always win over template defaults.
- Custom component types (e.g. `"PlayerController"`) must have a matching `PlayerController.cs` in `scripts/`.
- Tags and layers that are not Unity built-ins must be declared in `settings.tags` / `settings.layers`.
- Prop values that start with `#` must be 9-character hex colors (`#` + 8 hex digits): `#RRGGBBAA`.
- Prop values that start with `ref:` are GameObject ID references: `"ref:player"`.
- Array props must have 2 (Vector2), 3 (Vector3), or 4 (Vector4) numeric elements.

### Built-in templates

Use these in `"template"` to avoid declaring common component combinations manually:

| Template | Components created | Mappable props |
|---|---|---|
| `basic/transform` | Transform | `position`, `rotation`, `scale` |
| `basic/mesh` | MeshRenderer, MeshFilter, Transform | `meshType`, `color`, `castShadows` |
| `ui/canvas` | Canvas, CanvasScaler, GraphicRaycaster | `renderMode`, `sortingOrder`, `referenceResolution` |
| `ui/button` | RectTransform, Image, Button, TextMeshProUGUI | `label`, `labelColor`, `fontSize`, `bgColor`, `sizeDelta`, `anchoredPosition` |
| `ui/label` | RectTransform, TextMeshProUGUI | `text`, `color`, `fontSize`, `sizeDelta` |
| `physics/rigidbody` | Rigidbody, BoxCollider | `mass`, `useGravity`, `isKinematic` |
| `physics/trigger` | BoxCollider (isTrigger=true) | `size`, `center` |
| `audio/source` | AudioSource | `volume`, `loop`, `playOnAwake`, `spatialBlend` |

Call `GET /schema` to get the authoritative list with all `propMappings` and `componentTypes` for each template.

---

## Log Line Reference

Every log line emitted during SSE streaming follows these conventions. Lines prefixed with `[ERROR]` or `[WARN]` are always **UPPER CASE** — scan for these to detect problems quickly.

| Pattern | Severity | Meaning |
|---|---|---|
| `=== Step N/6: ... ===` | Info | Pipeline stage marker. 6 steps total. |
| `[ZipLoader] Extracting ...` | Info | ZIP is being extracted to temp dir |
| `[ZipLoader]   script ← Name.cs` | Info | A script file was discovered in `scripts/` |
| `[ZipLoader]   ⚠ duplicate script name ...` | Info | Duplicate script skipped |
| `[ProjectCreator] Creating project at ...` | Info | New Unity project folder is being scaffolded |
| `[ProjectCreator] Reusing existing project at ...` | Info | Project folder already exists; reusing it |
| `[Unity Pass 1] Starting ...` | Info | Unity is being launched for package import + compile |
| `[Unity Pass 2] Starting ...` | Info | Unity is being launched for scene generation |
| `[Unity] PID <n> started.` | Info | Unity process PID |
| `  [Unity] <line>` | Info | Raw line tailed from Unity's log file |
| `  ✓  ...` | Success | Step completed successfully |
| `  [WARN] <MESSAGE IN CAPS>` | Warning | Non-fatal. Pipeline continues. |
| `[ERROR] <MESSAGE IN CAPS>` | Error | Fatal. Pipeline will abort after this line. The final `event: error` SSE event follows. |
| `[ERROR] UNITY TIMEOUT AFTER N MIN ...` | Error | Unity did not complete within the time limit |
| `[ERROR] UNITY PRODUCED NO LOG OUTPUT FOR Ns ...` | Error | Unity appears hung (license issue, crash, dialog) |
| `[ERROR] UNITY LOG CONTAINS FAILURE PATTERN: '...'` | Error | A known failure string was found in Unity's log |

### Step markers and their meaning

```
=== Step 1/6: Loading scene zip ===
    Extracting ZIP, parsing scene.json, discovering scripts/.

=== Step 2/6: Resolving templates ===
    Expanding "template" shorthands into full component lists.

=== Step 3/6: Validating config ===
    Structural validation. Errors here abort before touching disk.

=== Step 4/6: Setting up project folder ===
    Creating Unity project directory structure, writing config files.

=== Step 5/6: Unity Pass 1 — Package import & compile ===
    Unity imports packages and compiles scripts. Longest step.
    Typical duration: 3–10 min on first run, 30 s on re-run.

=== Step 6/6: Unity Pass 2 — Scene generation ===
    Unity runs Builder.Run, creates scene files.
    Typical duration: 1–3 min.
```

---

## Common Errors and Fixes

| Error message | Likely cause | Fix |
|---|---|---|
| `sceneZipBase64 is required.` | Missing field in JSON body | Add `"sceneZipBase64"` field |
| `sceneZipBase64 is not valid base64.` | Encoding error | Re-encode ZIP as standard base64 |
| `scene.json not found.` | ZIP structure is wrong | Put `scene.json` at root of the ZIP |
| `scene.json parse error: ...` | Malformed JSON | Fix syntax errors in `scene.json` |
| `Config validation failed (N error(s)): ...` | Invalid `scene.json` content | Check `errors[]` in the response; fix each one |
| `Unity executable not found: ...` | Wrong path | Pass correct `unityExePath` or install Unity at the default path |
| `Application is currently busy.` | Another job is running | Poll `GET /status` and retry when `running` is `false` |
| `[ERROR] UNITY TIMEOUT AFTER 12 MIN` | Unity took too long | Check Unity log in `outputDir`; possible license or import issue |
| `[ERROR] UNITY PRODUCED NO LOG OUTPUT FOR 120S` | Unity hung | Unity likely hit a dialog or lost its license; check the machine |
| `[ERROR] UNITY LOG CONTAINS FAILURE PATTERN: 'ERROR CS'` | Script compile error | Fix C# errors in your `scripts/*.cs` files |
| `Unity exited cleanly but Builder never wrote its result file.` | Builder.cs crashed silently | Check `SceneGenUnity_Pass2.log` in `outputDir` |