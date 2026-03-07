using System.Collections.Generic;
using System.Linq;

namespace UnitySceneGen
{
    /// <summary>
    /// Builds the full machine-readable schema returned by GET /schema.
    /// Includes: prop format rules, script body formats, ID convention,
    /// built-in component catalog, and the full template catalog.
    ///
    /// An LLM fetches this once per session and has everything it needs
    /// to generate correct configs without guessing type names, prop names,
    /// enum values, or template names.
    /// </summary>
    public static class ComponentSchema
    {
        public static object Build() => new
        {
            version = "1.1",

            usage = new
            {
                summary =
                    "Fetch GET /schema once before generating. It tells you every component type, " +
                    "template, prop format, and valid value so you don't have to guess anything.",
                workflow = new[]
                {
                    "1. GET /schema           — learn what is available (this endpoint)",
                    "2. Build your config",
                    "3. POST /validate        — fix errors cheaply (< 1 s, no Unity needed)",
                    "4. POST /generate        — run the full pipeline (5–20 min)",
                    "5. GET /status           — poll progress while /generate is running",
                },
            },

            // ── Input format ──────────────────────────────────────────
            inputFormat = new
            {
                summary =
                    "Send a base64-encoded scene.zip to POST /generate (or POST /validate). " +
                    "The zip must contain scene.json at its root and an optional scripts/ folder.",

                zipStructure = new[]
                {
                    "scene.zip",
                    "├── scene.json          ← required: all scene data in one file",
                    "└── scripts/            ← optional: .cs files copied as-is into Assets/Scripts/",
                    "    ├── PlayerController.cs",
                    "    └── Rotator.cs",
                },

                sceneJsonFields = new
                {
                    project     = "ProjectConfig — name, unityVersion, packages",
                    settings    = "SettingsConfig — custom tags[], layers[]",
                    scenes      = "SceneConfig[] — name, path, roots[]",
                    gameObjects = "GameObjectConfig[] — id, name, tag, layer, children[], components[], template, templateProps",
                    scripts     = "ScriptConfig[] — optional. Scripts in scripts/ are auto-discovered. Use body[] for inline C#.",
                },

                scriptsFolder = new
                {
                    description =
                        "All .cs files in scripts/ are auto-discovered and copied directly into " +
                        "Assets/Scripts/ without wrapping. The class name is the filename without extension. " +
                        "You do NOT need to list them in scene.json — discovery is automatic.",
                    example = "scripts/PlayerController.cs  →  Assets/Scripts/PlayerController.cs  →  type: \"PlayerController\"",
                },

                sceneJsonExample = new
                {
                    project     = new { name = "MyProject", unityVersion = "2022.3.20f1" },
                    settings    = new { tags = new[] { "Player" }, layers = new[] { "Gameplay" } },
                    scenes      = new[] { new { name = "Main", path = "Assets/Scenes/Main.unity", roots = new[] { "go.root" } } },
                    gameObjects = new object[]
                    {
                        new { id = "go.root",        name = "Root",        children = new[] { "go.root.camera" } },
                        new { id = "go.root.camera", name = "Main Camera", tag = "MainCamera", template = "camera/main" },
                    },
                },
            },

            // ── Prop value formats ────────────────────────────────────
            propFormats = new
            {
                Color   = "#RRGGBBAA — exactly 8 hex digits, alpha always required. Examples: \"#FFFFFFFF\" (opaque white), \"#FF000080\" (50% red), \"#00000000\" (transparent).",
                Vector2 = "[x, y] — e.g. [0.5, 0.5]",
                Vector3 = "[x, y, z] — e.g. [0, 1, 0]",
                Vector4 = "[x, y, z, w] — e.g. [0, 0, 0, 1]",
                @ref    = "\"ref:<gameObjectId>\" — references another GameObject by id. e.g. \"ref:go.camera\"",
                @enum   = "A plain string matching the enum value name exactly (case-sensitive).",
                @float  = "A JSON number. Integer literals are accepted for float fields.",
                @int    = "A JSON integer.",
                @bool   = "true or false (JSON boolean, not a string).",
                @string = "A JSON string.",
            },

            // ── Script body formats ───────────────────────────────────
            scriptBody = new
            {
                recommended = new
                {
                    format  = "Array of strings — one element per line. No escaping needed.",
                    example = new
                    {
                        name = "Rotator",
                        body = new[]
                        {
                            "    [SerializeField] float speed = 90f;",
                            "",
                            "    void Update()",
                            "    {",
                            "        transform.Rotate(Vector3.up * speed * Time.deltaTime);",
                            "    }",
                        },
                    },
                },
                externalFile = new
                {
                    format   = "A real .cs file in the scripts/ folder of your scene.zip.",
                    howItWorks = "Drop .cs files in scripts/ — ZipLoader discovers them automatically. No need to list them in scene.json.",
                    notes    = "The file is copied directly — it must be a complete, valid .cs file. No MonoBehaviour wrapping is added.",
                },
                legacy = new
                {
                    format  = "Plain string with \\n for newlines (error-prone, not recommended).",
                    example = "    void Start() { }\\n\\n    void Update() { }",
                },
                wrapping = new[]
                {
                    "UnitySceneGen auto-wraps body-based scripts: public class <name> : MonoBehaviour { <body> }",
                    "Set 'namespace' to also wrap in: namespace <namespace> { ... }",
                    "External .cs files (file:) are NOT wrapped — they must be complete files.",
                    "Leave 'body' empty for a stub with Start() and Update() methods.",
                },
            },

            // ── ID naming convention ──────────────────────────────────
            idConvention = new
            {
                rule =
                    "A child's id must start with its parent's id followed by a dot. " +
                    "This makes the hierarchy self-documenting — you can infer parent/child " +
                    "relationships from ids alone without reading the children arrays.",
                examples = new[]
                {
                    "go.root",
                    "go.root.camera       ← child of go.root",
                    "go.root.light        ← child of go.root",
                    "go.ui                ← root-level",
                    "go.ui.panel          ← child of go.ui",
                    "go.ui.panel.title    ← child of go.ui.panel",
                    "go.ui.panel.subtitle ← child of go.ui.panel",
                    "go.world.player",
                    "go.world.player.weapon",
                },
                enforcement = "A warning is produced if a child id does not start with parent id + '.'. Generation still proceeds.",
            },

            // ── Built-in tags / layers ────────────────────────────────
            builtinTags = new[]
            {
                "Untagged", "Respawn", "Finish", "EditorOnly",
                "MainCamera", "Player", "GameController",
            },
            builtinLayers = new[]
            {
                "Default", "TransparentFX", "Ignore Raycast", "Water", "UI",
            },

            // ── Template catalog ──────────────────────────────────────
            templates = new
            {
                usage = new[]
                {
                    "Add \"template\": \"<name>\" to any GameObject to expand a set of components automatically.",
                    "Override specific props with \"templateProps\": { \"<friendlyKey>\": <value> }",
                    "Explicit \"components\" on the same GameObject are merged on top — explicit always wins.",
                    "After resolution the template field is cleared; the validator sees only the expanded components.",
                },
                example = new
                {
                    id            = "go.ui.start_btn",
                    name          = "Start Button",
                    layer         = "UI",
                    template      = "ui/button",
                    templateProps = new { label = "Start Game", bgColor = "#1A73E8FF", sizeDelta = new[] { 220, 60 } },
                },
                builtins = TemplateResolver.GetBuiltins().ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        description  = kv.Value.Description,
                        propMappings = kv.Value.PropMappings,
                        componentTypes = kv.Value.Components.Select(c => c.Type).ToArray(),
                    }),
            },

            // ── Component catalog ─────────────────────────────────────
            components = new Dictionary<string, object>
            {
                ["UnityEngine.Transform"] = Comp(
                    notes: new[] {
                        "Every GameObject already has a Transform — do NOT add it with AddComponent.",
                        "List it as a component only to set position, rotation, or scale via props.",
                        "All values are local-space (relative to parent).",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["localPosition"]    = P("Vector3", def: "[0, 0, 0]"),
                        ["localEulerAngles"] = P("Vector3", def: "[0, 0, 0]", notes: "Rotation in degrees."),
                        ["localScale"]       = P("Vector3", def: "[1, 1, 1]"),
                    }),

                ["UnityEngine.Camera"] = Comp(
                    notes: new[] {
                        "Pair with UnityEngine.AudioListener on the same GameObject.",
                        "Exactly one camera should have tag 'MainCamera'.",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["clearFlags"]      = P("enum",  def: "Skybox",   vals: new[] { "Skybox","SolidColor","DepthOnly","Nothing" }),
                        ["backgroundColor"] = P("Color", def: "#19191AFF",notes: "Only visible when clearFlags = SolidColor."),
                        ["fieldOfView"]     = P("float", def: "60",       range: new[] { 1, 179 }),
                        ["nearClipPlane"]   = P("float", def: "0.3"),
                        ["farClipPlane"]    = P("float", def: "1000"),
                        ["orthographic"]    = P("bool",  def: "false"),
                        ["depth"]           = P("float", def: "-1",       notes: "Higher draws on top."),
                    }),

                ["UnityEngine.AudioListener"] = Comp(
                    notes: new[] { "No props. One per scene — usually on the main camera." }),

                ["UnityEngine.Light"] = Comp(
                    notes: new[] { "Use localEulerAngles [50, -30, 0] on Transform for a natural sun angle." },
                    props: new Dictionary<string, object>
                    {
                        ["type"]      = P("enum",  def: "Directional", vals: new[] { "Directional","Point","Spot","Area" }),
                        ["color"]     = P("Color", def: "#FFFFFFFF"),
                        ["intensity"] = P("float", def: "1",    range: new[] { 0, 8 }),
                        ["shadows"]   = P("enum",  def: "None", vals: new[] { "None","Hard","Soft" }),
                        ["range"]     = P("float", def: "10",   notes: "Point and Spot only."),
                        ["spotAngle"] = P("float", def: "30",   notes: "Spot only.", range: new[] { 1, 179 }),
                    }),

                ["UnityEngine.Rigidbody"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["mass"]                  = P("float", def: "1"),
                        ["drag"]                  = P("float", def: "0"),
                        ["useGravity"]            = P("bool",  def: "true"),
                        ["isKinematic"]           = P("bool",  def: "false"),
                        ["collisionDetectionMode"] = P("enum", def: "Discrete",
                            vals: new[] { "Discrete","Continuous","ContinuousDynamic","ContinuousSpeculative" },
                            notes: "Use ContinuousDynamic for fast-moving objects."),
                    }),

                ["UnityEngine.BoxCollider"]     = Comp(props: ColliderProps(hasSize: true)),
                ["UnityEngine.SphereCollider"]  = Comp(props: ColliderProps(hasRadius: true)),
                ["UnityEngine.CapsuleCollider"] = Comp(props: ColliderProps(hasCapsule: true)),
                ["UnityEngine.MeshCollider"]    = Comp(
                    notes: new[] { "convex must be true when used with a non-kinematic Rigidbody." },
                    props: new Dictionary<string, object>
                    {
                        ["isTrigger"] = P("bool", def: "false"),
                        ["convex"]    = P("bool", def: "false"),
                    }),

                ["UnityEngine.Canvas"] = Comp(
                    notes: new[] {
                        "MUST be the first component listed — it promotes Transform to RectTransform.",
                        "Pair with CanvasScaler and GraphicRaycaster on the same GameObject.",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["renderMode"]   = P("enum", def: "ScreenSpaceOverlay",
                            vals: new[] { "ScreenSpaceOverlay","ScreenSpaceCamera","WorldSpace" }),
                        ["sortingOrder"] = P("int",  def: "0", notes: "Higher renders on top."),
                    }),

                ["UnityEngine.UI.CanvasScaler"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["uiScaleMode"]        = P("enum", def: "ConstantPixelSize",
                            vals: new[] { "ConstantPixelSize","ScaleWithScreenSize","ConstantPhysicalSize" }),
                        ["referenceResolution"] = P("Vector2", def: "[1920, 1080]",
                            notes: "Only when uiScaleMode = ScaleWithScreenSize."),
                        ["matchWidthOrHeight"]  = P("float", def: "0", range: new[] { 0, 1 },
                            notes: "0 = match width, 1 = match height."),
                    }),

                ["UnityEngine.UI.GraphicRaycaster"] = Comp(
                    notes: new[] { "No props needed. Required for click events on UI elements." }),

                ["UnityEngine.RectTransform"] = Comp(
                    notes: new[] {
                        "Canvas must come before RectTransform in the component list.",
                        "Fill parent: anchorMin [0,0], anchorMax [1,1], offsetMin [0,0], offsetMax [0,0].",
                        "Fixed size at centre: anchorMin/Max [0.5,0.5], sizeDelta [w,h], anchoredPosition [0,0].",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["anchorMin"]        = P("Vector2", def: "[0.5, 0.5]", notes: "0=bottom/left, 1=top/right."),
                        ["anchorMax"]        = P("Vector2", def: "[0.5, 0.5]"),
                        ["anchoredPosition"] = P("Vector2", def: "[0, 0]"),
                        ["sizeDelta"]        = P("Vector2", def: "[100, 100]"),
                        ["offsetMin"]        = P("Vector2", def: "[0, 0]"),
                        ["offsetMax"]        = P("Vector2", def: "[0, 0]"),
                        ["pivot"]            = P("Vector2", def: "[0.5, 0.5]", notes: "[0.5,0.5]=centre."),
                        ["localEulerAngles"] = P("Vector3", def: "[0, 0, 0]"),
                        ["localScale"]       = P("Vector3", def: "[1, 1, 1]"),
                    }),

                ["UnityEngine.UI.Image"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["color"]         = P("Color", def: "#FFFFFFFF"),
                        ["raycastTarget"] = P("bool",  def: "true",  notes: "Set false on decorative images."),
                        ["fillAmount"]    = P("float", def: "1",     range: new[] { 0, 1 }),
                    }),

                ["UnityEngine.UI.Button"] = Comp(
                    notes: new[] { "Requires Image on same GameObject. Wire onClick via a MonoBehaviour script." },
                    props: new Dictionary<string, object>
                    {
                        ["interactable"] = P("bool", def: "true"),
                    }),

                ["TMPro.TextMeshProUGUI"] = Comp(
                    notes: new[] { "Preferred over UnityEngine.UI.Text for all new projects." },
                    props: new Dictionary<string, object>
                    {
                        ["text"]               = P("string", def: ""),
                        ["fontSize"]           = P("float",  def: "36"),
                        ["color"]              = P("Color",  def: "#FFFFFFFF"),
                        ["alignment"]          = P("enum",   def: "TopLeft",
                            vals: new[] { "TopLeft","Top","TopRight","Left","Center","Right","BottomLeft","Bottom","BottomRight" }),
                        ["enableWordWrapping"] = P("bool",   def: "true"),
                        ["overflowMode"]       = P("enum",   def: "Overflow",
                            vals: new[] { "Overflow","Ellipsis","Masking","Truncate" }),
                        ["fontStyle"]          = P("enum",   def: "Normal",
                            vals: new[] { "Normal","Bold","Italic","Underline","Strikethrough" }),
                    }),

                ["UnityEngine.EventSystems.EventSystem"]          = Comp(notes: new[] { "One per scene. Always pair with StandaloneInputModule." }),
                ["UnityEngine.EventSystems.StandaloneInputModule"] = Comp(notes: new[] { "Always pair with EventSystem." }),

                ["UnityEngine.AudioSource"] = Comp(
                    notes: new[] { "Assign clip at runtime via script. spatialBlend: 0=2D, 1=3D." },
                    props: new Dictionary<string, object>
                    {
                        ["volume"]       = P("float", def: "1",     range: new[] { 0, 1 }),
                        ["pitch"]        = P("float", def: "1",     range: new[] { -3, 3 }),
                        ["loop"]         = P("bool",  def: "false"),
                        ["playOnAwake"]  = P("bool",  def: "true"),
                        ["spatialBlend"] = P("float", def: "0",     range: new[] { 0, 1 }),
                    }),

                ["UnityEngine.Animator"] = Comp(
                    notes: new[] { "Assign AnimatorController at runtime via script." },
                    props: new Dictionary<string, object>
                    {
                        ["applyRootMotion"] = P("bool", def: "false"),
                        ["updateMode"]      = P("enum", def: "Normal",
                            vals: new[] { "Normal","AnimatePhysics","UnscaledTime" }),
                    }),
            },
        };

        // ── Schema builder helpers ────────────────────────────────────

        private static object Comp(
            string[]? notes = null,
            Dictionary<string, object>? props = null) => new
        {
            notes = notes ?? System.Array.Empty<string>(),
            props = props ?? new Dictionary<string, object>(),
        };

        private static object P(
            string type,
            string? def   = null,
            int[]?  range = null,
            string[]? vals  = null,
            string? notes = null) => new
        {
            type         = type,
            @default     = def,
            range        = range,
            values       = vals,
            notes        = notes,
        };

        private static Dictionary<string, object> ColliderProps(
            bool hasSize = false, bool hasRadius = false, bool hasCapsule = false)
        {
            var d = new Dictionary<string, object>
            {
                ["isTrigger"] = P("bool",    def: "false"),
                ["center"]    = P("Vector3", def: "[0, 0, 0]"),
            };
            if (hasSize)    d["size"]      = P("Vector3", def: "[1, 1, 1]");
            if (hasRadius)  d["radius"]    = P("float",   def: "0.5");
            if (hasCapsule) { d["radius"]  = P("float",   def: "0.5"); d["height"] = P("float", def: "2"); }
            return d;
        }
    }
}
