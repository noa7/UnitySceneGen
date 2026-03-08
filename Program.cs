// ═══════════════════════════════════════════════════════════════════════════
// UnitySceneGen — single-file build
//
// PIPELINE (one strategy, no alternatives):
//   Input:  scene.zip  →  scene.json + scripts/ folder
//   Output: generated Unity project .zip
//
// ZIP CONTRACT:
//   scene.zip
//   ├── scene.json          ← wiring only (project, settings, scenes, gameObjects)
//   ├── scripts/            ← .cs files, auto-discovered, copied as-is
//   └── templates/          ← optional .json template files
//
// scene.json NEVER contains inline C# code or file paths.
// Scripts are always physical .cs files in scripts/.
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UnitySceneGen
{

    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 1 — MODELS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Root config object. Deserialised from scene.json inside the zip.
    /// Scripts are NOT declared here — they are discovered automatically
    /// from the scripts/ folder by ZipLoader.
    /// </summary>
    public class SceneGenConfig
    {
        [JsonPropertyName("project")] public ProjectConfig? Project { get; set; }
        [JsonPropertyName("settings")] public SettingsConfig? Settings { get; set; }
        [JsonPropertyName("scenes")] public List<SceneConfig> Scenes { get; set; } = new();
        [JsonPropertyName("gameObjects")] public List<GameObjectConfig> GameObjects { get; set; } = new();

        /// <summary>
        /// Populated by ZipLoader after extraction — never read from scene.json.
        /// Not serialised to the Unity-side SceneGenConfig.json either;
        /// Unity only needs the compiled assemblies, not the source list.
        /// </summary>
        [JsonIgnore]
        public List<ScriptEntry> Scripts { get; set; } = new();
    }

    /// <summary>
    /// A discovered .cs file from the scripts/ folder.
    /// Name  = class name (filename without extension).
    /// AbsolutePath = full path inside the temp extract dir — used by ProjectCreator to copy.
    /// </summary>
    public class ScriptEntry
    {
        public string Name { get; set; } = "";
        public string AbsolutePath { get; set; } = "";
    }

    public class ProjectConfig
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "MyProject";
        [JsonPropertyName("unityVersion")] public string UnityVersion { get; set; } = "2022.3.20f1";
        [JsonPropertyName("packages")] public List<string> Packages { get; set; } = new();
    }

    public class SettingsConfig
    {
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
        [JsonPropertyName("layers")] public List<string> Layers { get; set; } = new();
    }

    public class SceneConfig
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "Main";
        [JsonPropertyName("path")] public string Path { get; set; } = "Assets/Scenes/Main.unity";
        [JsonPropertyName("roots")] public List<string> Roots { get; set; } = new();
    }

    public class GameObjectConfig
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "GameObject";
        [JsonPropertyName("active")] public bool Active { get; set; } = true;
        [JsonPropertyName("tag")] public string Tag { get; set; } = "Untagged";
        [JsonPropertyName("layer")] public string Layer { get; set; } = "Default";
        [JsonPropertyName("children")] public List<string> Children { get; set; } = new();
        [JsonPropertyName("components")] public List<ComponentConfig> Components { get; set; } = new();

        /// <summary>
        /// Built-in or file-based template name. Expanded by TemplateResolver before
        /// validation. Cleared after expansion — the validator only sees the full
        /// component list.
        /// </summary>
        [JsonPropertyName("template")] public string? Template { get; set; }

        /// <summary>
        /// Friendly prop overrides applied during template expansion.
        /// Keys are the template's declared prop-mapping names.
        /// </summary>
        [JsonPropertyName("templateProps")] public Dictionary<string, object>? TemplateProps { get; set; }
    }

    public class ComponentConfig
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("props")] public Dictionary<string, object>? Props { get; set; }
    }

    // ── Generation I/O ────────────────────────────────────────────────────

    public class GenerationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string SceneGenOutputPath { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
    }

    public class BuilderResult
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; } = "";
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
        [JsonPropertyName("scenes")] public List<string> Scenes { get; set; } = new();
    }

    public class GenerationStatus
    {
        [JsonPropertyName("running")] public bool Running { get; set; }
        [JsonPropertyName("step")] public string Step { get; set; } = "Idle";
        [JsonPropertyName("error")] public string Error { get; set; } = "";
        [JsonPropertyName("log")] public List<string> Log { get; set; } = new();
    }

    // ── Template definition ───────────────────────────────────────────────

    public class TemplateDefinition
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("components")] public List<ComponentConfig> Components { get; set; } = new();
        [JsonPropertyName("propMappings")] public Dictionary<string, string> PropMappings { get; set; } = new();
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 2 — APP SETTINGS
    // ═══════════════════════════════════════════════════════════════════════════

    public class AppSettings
    {
        public const string DefaultUnityExePath =
            @"C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe";

        public string? LastZipPath { get; set; }
        public string? LastUnityExePath { get; set; }
        public string? LastOutputDir { get; set; }

        private static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnitySceneGen", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<AppSettings>(
                               File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(this, Json.Pretty));
            }
            catch { }
        }
    }

    // ── Shared JSON options ───────────────────────────────────────────────────
    internal static class Json
    {
        /// <summary>Indented output, camelCase property names ignored (we use [JsonPropertyName] everywhere).</summary>
        public static readonly JsonSerializerOptions Pretty = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>Compact output for SSE payloads.</summary>
        public static readonly JsonSerializerOptions Compact = new()
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 2b — PROCESSING GATE
    //
    // Shared mutex enforcing the "one job at a time" rule for both the WPF UI
    // and the HTTP API.  Whoever holds the token owns the pipeline; the other
    // side receives a 503 / busy-message instead.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Thread-safe, one-slot semaphore shared by <see cref="MainWindow"/> and
    /// <see cref="ApiServer"/>.  The returned <see cref="IDisposable"/> token
    /// must be disposed when the job finishes; disposal raises
    /// <see cref="BusyChanged"/> so the UI can re-enable itself.
    /// </summary>
    public sealed class ProcessingGate
    {
        private readonly SemaphoreSlim _sem = new(1, 1);

        /// <summary>Fired on the thread that acquires or releases the slot.</summary>
        public event Action<bool>? BusyChanged;

        /// <summary>True while a job holds the slot.</summary>
        public bool IsBusy => _sem.CurrentCount == 0;

        /// <summary>
        /// Try to acquire the slot synchronously.
        /// Returns a release-token on success, or <c>null</c> if already taken.
        /// </summary>
        public IDisposable? TryAcquire()
        {
            if (!_sem.Wait(0)) return null;
            BusyChanged?.Invoke(true);
            return new ReleaseToken(this);
        }

        private void Release()
        {
            _sem.Release();
            BusyChanged?.Invoke(false);
        }

        private sealed class ReleaseToken : IDisposable
        {
            private readonly ProcessingGate _gate;
            private int _disposed;
            public ReleaseToken(ProcessingGate gate) => _gate = gate;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _gate.Release();
            }
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 3 — ZIP LOADER
    //
    // Single entry point for all input. No other loading mechanism exists.
    //
    // ZIP CONTRACT:
    //   scene.zip
    //   ├── scene.json        ← required, at root or one folder deep
    //   ├── scripts/          ← optional, .cs files auto-discovered recursively
    //   └── templates/        ← optional, .json template files auto-detected
    // ═══════════════════════════════════════════════════════════════════════════

    public static class ZipLoader
    {
        public record LoadResult(
            SceneGenConfig Config,
            string ExtractDir,    // temp dir — caller must delete when done
            string SceneJsonDir,  // dir containing scene.json
            string? TemplatesDir); // templates/ dir if present

        /// <summary>
        /// Extracts zipBytes to a temp directory and loads the scene config.
        /// Scripts are discovered automatically from the scripts/ subfolder.
        /// </summary>
        public static LoadResult Load(byte[] zipBytes, Action<string>? log = null)
        {
            var extractDir = Path.Combine(
                Path.GetTempPath(), $"UnitySceneGen_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);

            log?.Invoke($"[ZipLoader] Extracting {zipBytes.Length / 1024:N0} KB to {extractDir}");

            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                zip.ExtractToDirectory(extractDir, overwriteFiles: true);

            return LoadFromDir(extractDir, log);
        }

        public static LoadResult LoadFromDir(string extractDir, Action<string>? log = null)
        {
            // ── Locate scene.json ─────────────────────────────────────────
            var sceneJsonPath = FindSceneJson(extractDir)
                ?? throw new FileNotFoundException(
                    "scene.json not found. " +
                    "Place it at the root of your zip: scene.zip/scene.json");

            var sceneJsonDir = Path.GetDirectoryName(sceneJsonPath)!;
            log?.Invoke($"[ZipLoader] Found scene.json: {sceneJsonPath}");

            // ── Deserialise scene.json ────────────────────────────────────
            SceneGenConfig cfg;
            try
            {
                cfg = JsonSerializer.Deserialize<SceneGenConfig>(
                          File.ReadAllText(sceneJsonPath), Json.Pretty)
                      ?? throw new InvalidDataException("scene.json deserialised to null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"scene.json parse error: {ex.Message}");
            }

            // ── Discover scripts/ folder ──────────────────────────────────
            // Scripts are NEVER declared in scene.json. Any .cs file found
            // under scripts/ is automatically included. This is the only way
            // to add scripts — no inline bodies, no file path references.
            var scriptsDir = Path.Combine(sceneJsonDir, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var csFile in Directory.EnumerateFiles(
                             scriptsDir, "*.cs", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(csFile);
                    if (seen.Add(name))
                    {
                        cfg.Scripts.Add(new ScriptEntry { Name = name, AbsolutePath = csFile });
                        log?.Invoke($"[ZipLoader]   script ← {name}.cs");
                    }
                    else
                    {
                        log?.Invoke($"[ZipLoader]   ⚠ duplicate script name '{name}' skipped.");
                    }
                }
            }

            // ── Auto-detect templates/ folder ─────────────────────────────
            string? templatesDir = null;
            var tDir = Path.Combine(sceneJsonDir, "templates");
            if (Directory.Exists(tDir))
            {
                templatesDir = tDir;
                log?.Invoke($"[ZipLoader]   templates/ found: {tDir}");
            }

            log?.Invoke($"[ZipLoader] Loaded: {cfg.Scenes.Count} scene(s), " +
                        $"{cfg.GameObjects.Count} object(s), {cfg.Scripts.Count} script(s).");

            return new LoadResult(cfg, extractDir, sceneJsonDir, templatesDir);
        }

        private static string? FindSceneJson(string dir)
        {
            var direct = Path.Combine(dir, "scene.json");
            if (File.Exists(direct)) return direct;

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var nested = Path.Combine(sub, "scene.json");
                if (File.Exists(nested)) return nested;
            }
            return null;
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 4 — TEMPLATE RESOLVER
    // ═══════════════════════════════════════════════════════════════════════════

    public static class TemplateResolver
    {
        // ── Built-in template library ─────────────────────────────────────
        private static readonly Dictionary<string, TemplateDefinition> Builtins =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["camera/main"] = new TemplateDefinition
                {
                    Name = "camera/main",
                    Description = "Main camera with AudioListener. Set tag to MainCamera.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.Camera", Props = new()
                        {
                            ["clearFlags"]    = "Skybox",
                            ["fieldOfView"]   = 60.0,
                            ["nearClipPlane"] = 0.3,
                            ["farClipPlane"]  = 1000.0,
                        }},
                        new() { Type = "UnityEngine.AudioListener" },
                    },
                    PropMappings = new()
                    {
                        ["clearFlags"] = "UnityEngine.Camera.clearFlags",
                        ["backgroundColor"] = "UnityEngine.Camera.backgroundColor",
                        ["fieldOfView"] = "UnityEngine.Camera.fieldOfView",
                        ["nearClipPlane"] = "UnityEngine.Camera.nearClipPlane",
                        ["farClipPlane"] = "UnityEngine.Camera.farClipPlane",
                    },
                },

                ["light/directional"] = new TemplateDefinition
                {
                    Name = "light/directional",
                    Description = "Directional light. Pair with Transform localEulerAngles [50,-30,0].",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.Light", Props = new()
                        {
                            ["type"]      = "Directional",
                            ["color"]     = "#FFFFFFFF",
                            ["intensity"] = 1.0,
                            ["shadows"]   = "Soft",
                        }},
                    },
                    PropMappings = new()
                    {
                        ["color"] = "UnityEngine.Light.color",
                        ["intensity"] = "UnityEngine.Light.intensity",
                        ["shadows"] = "UnityEngine.Light.shadows",
                    },
                },

                ["ui/canvas"] = new TemplateDefinition
                {
                    Name = "ui/canvas",
                    Description = "Full-screen overlay Canvas with CanvasScaler (1920x1080) and GraphicRaycaster.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.Canvas", Props = new()
                        {
                            ["renderMode"] = "ScreenSpaceOverlay",
                        }},
                        new() { Type = "UnityEngine.UI.CanvasScaler", Props = new()
                        {
                            ["uiScaleMode"]         = "ScaleWithScreenSize",
                            ["referenceResolution"]  = new[] { 1920, 1080 },
                            ["matchWidthOrHeight"]   = 0.5,
                        }},
                        new() { Type = "UnityEngine.UI.GraphicRaycaster" },
                    },
                    PropMappings = new()
                    {
                        ["referenceResolution"] = "UnityEngine.UI.CanvasScaler.referenceResolution",
                        ["matchWidthOrHeight"] = "UnityEngine.UI.CanvasScaler.matchWidthOrHeight",
                    },
                },

                ["ui/eventsystem"] = new TemplateDefinition
                {
                    Name = "ui/eventsystem",
                    Description = "EventSystem + StandaloneInputModule. Add exactly one per scene.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.EventSystems.EventSystem" },
                        new() { Type = "UnityEngine.EventSystems.StandaloneInputModule" },
                    },
                    PropMappings = new(),
                },

                ["ui/panel"] = new TemplateDefinition
                {
                    Name = "ui/panel",
                    Description = "RectTransform filling parent + Image background.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.RectTransform", Props = new()
                        {
                            ["anchorMin"] = new[] { 0, 0 },
                            ["anchorMax"] = new[] { 1, 1 },
                            ["offsetMin"] = new[] { 0, 0 },
                            ["offsetMax"] = new[] { 0, 0 },
                        }},
                        new() { Type = "UnityEngine.UI.Image", Props = new()
                        {
                            ["color"] = "#00000080",
                        }},
                    },
                    PropMappings = new()
                    {
                        ["bgColor"] = "UnityEngine.UI.Image.color",
                        ["anchorMin"] = "UnityEngine.RectTransform.anchorMin",
                        ["anchorMax"] = "UnityEngine.RectTransform.anchorMax",
                        ["offsetMin"] = "UnityEngine.RectTransform.offsetMin",
                        ["offsetMax"] = "UnityEngine.RectTransform.offsetMax",
                    },
                },

                ["ui/button"] = new TemplateDefinition
                {
                    Name = "ui/button",
                    Description = "Clickable button: RectTransform + Image + Button + TextMeshProUGUI label.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.RectTransform", Props = new()
                        {
                            ["anchorMin"]        = new[] { 0.5, 0.5 },
                            ["anchorMax"]        = new[] { 0.5, 0.5 },
                            ["sizeDelta"]        = new[] { 200, 50 },
                            ["anchoredPosition"] = new[] { 0, 0 },
                        }},
                        new() { Type = "UnityEngine.UI.Image", Props = new()
                        {
                            ["color"] = "#1A73E8FF",
                        }},
                        new() { Type = "UnityEngine.UI.Button", Props = new()
                        {
                            ["interactable"] = true,
                        }},
                        new() { Type = "TMPro.TextMeshProUGUI", Props = new()
                        {
                            ["text"]      = "Button",
                            ["fontSize"]  = 24.0,
                            ["color"]     = "#FFFFFFFF",
                            ["alignment"] = "Center",
                        }},
                    },
                    PropMappings = new()
                    {
                        ["label"] = "TMPro.TextMeshProUGUI.text",
                        ["labelColor"] = "TMPro.TextMeshProUGUI.color",
                        ["fontSize"] = "TMPro.TextMeshProUGUI.fontSize",
                        ["bgColor"] = "UnityEngine.UI.Image.color",
                        ["sizeDelta"] = "UnityEngine.RectTransform.sizeDelta",
                        ["anchoredPosition"] = "UnityEngine.RectTransform.anchoredPosition",
                    },
                },

                ["ui/label"] = new TemplateDefinition
                {
                    Name = "ui/label",
                    Description = "RectTransform + TextMeshProUGUI.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.RectTransform", Props = new()
                        {
                            ["anchorMin"]  = new[] { 0.5, 0.5 },
                            ["anchorMax"]  = new[] { 0.5, 0.5 },
                            ["sizeDelta"]  = new[] { 200, 50 },
                        }},
                        new() { Type = "TMPro.TextMeshProUGUI", Props = new()
                        {
                            ["text"]      = "Label",
                            ["fontSize"]  = 24.0,
                            ["color"]     = "#FFFFFFFF",
                            ["alignment"] = "Center",
                        }},
                    },
                    PropMappings = new()
                    {
                        ["text"] = "TMPro.TextMeshProUGUI.text",
                        ["color"] = "TMPro.TextMeshProUGUI.color",
                        ["fontSize"] = "TMPro.TextMeshProUGUI.fontSize",
                        ["sizeDelta"] = "UnityEngine.RectTransform.sizeDelta",
                    },
                },

                ["physics/rigidbody"] = new TemplateDefinition
                {
                    Name = "physics/rigidbody",
                    Description = "Rigidbody + BoxCollider.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.Rigidbody", Props = new()
                        {
                            ["mass"]       = 1.0,
                            ["useGravity"] = true,
                        }},
                        new() { Type = "UnityEngine.BoxCollider" },
                    },
                    PropMappings = new()
                    {
                        ["mass"] = "UnityEngine.Rigidbody.mass",
                        ["useGravity"] = "UnityEngine.Rigidbody.useGravity",
                        ["isKinematic"] = "UnityEngine.Rigidbody.isKinematic",
                    },
                },

                ["physics/trigger"] = new TemplateDefinition
                {
                    Name = "physics/trigger",
                    Description = "BoxCollider with isTrigger = true.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.BoxCollider", Props = new()
                        {
                            ["isTrigger"] = true,
                        }},
                    },
                    PropMappings = new()
                    {
                        ["size"] = "UnityEngine.BoxCollider.size",
                        ["center"] = "UnityEngine.BoxCollider.center",
                    },
                },

                ["audio/source"] = new TemplateDefinition
                {
                    Name = "audio/source",
                    Description = "AudioSource. Assign clip at runtime via script.",
                    Components = new List<ComponentConfig>
                    {
                        new() { Type = "UnityEngine.AudioSource", Props = new()
                        {
                            ["volume"]      = 1.0,
                            ["playOnAwake"] = false,
                            ["loop"]        = false,
                        }},
                    },
                    PropMappings = new()
                    {
                        ["volume"] = "UnityEngine.AudioSource.volume",
                        ["loop"] = "UnityEngine.AudioSource.loop",
                        ["playOnAwake"] = "UnityEngine.AudioSource.playOnAwake",
                        ["spatialBlend"] = "UnityEngine.AudioSource.spatialBlend",
                    },
                },
            };

        // ── Public API ────────────────────────────────────────────────────

        public static void Resolve(
            SceneGenConfig cfg,
            string? templatesDir = null,
            List<string>? warnings = null)
        {
            var fileCache = templatesDir != null
                ? LoadFileTemplates(templatesDir, warnings)
                : new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var go in cfg.GameObjects)
            {
                if (string.IsNullOrWhiteSpace(go.Template)) continue;

                var tpl = FindTemplate(go.Template, fileCache);
                if (tpl == null)
                {
                    warnings?.Add(
                        $"Template '{go.Template}' on '{go.Id}' not found. " +
                        $"Built-ins: {string.Join(", ", Builtins.Keys)}.");
                    go.Template = null;
                    go.TemplateProps = null;
                    continue;
                }

                // Deep-copy template components as the base
                var resolved = tpl.Components
                    .Select(c => new ComponentConfig
                    {
                        Type = c.Type,
                        Props = c.Props == null ? null : new Dictionary<string, object>(c.Props),
                    })
                    .ToList();

                // Apply templateProps via propMappings
                if (go.TemplateProps != null)
                {
                    foreach (var (key, val) in go.TemplateProps)
                    {
                        if (!tpl.PropMappings.TryGetValue(key, out var mapping))
                        {
                            warnings?.Add(
                                $"'{go.Id}' templateProp '{key}' is not mapped in template '{go.Template}'. " +
                                $"Available: {string.Join(", ", tpl.PropMappings.Keys)}.");
                            continue;
                        }

                        var dot = mapping.LastIndexOf('.');
                        var compType = mapping[..dot];
                        var propName = mapping[(dot + 1)..];

                        var comp = resolved.FirstOrDefault(c =>
                            string.Equals(c.Type, compType, StringComparison.OrdinalIgnoreCase));

                        if (comp == null)
                        {
                            warnings?.Add($"'{go.Id}' propMapping '{key}' targets '{compType}' " +
                                          $"which is not in template '{go.Template}'.");
                            continue;
                        }

                        comp.Props ??= new Dictionary<string, object>();
                        comp.Props[propName] = val;
                    }
                }

                // Merge explicit components on top — explicit always wins by type
                foreach (var oc in go.Components)
                {
                    var existing = resolved.FirstOrDefault(c =>
                        string.Equals(c.Type, oc.Type, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        if (oc.Props != null)
                        {
                            existing.Props ??= new Dictionary<string, object>();
                            foreach (var (k, v) in oc.Props)
                                existing.Props[k] = v;
                        }
                    }
                    else
                    {
                        resolved.Add(oc);
                    }
                }

                go.Components = resolved;
                go.Template = null;
                go.TemplateProps = null;
            }
        }

        public static IReadOnlyDictionary<string, TemplateDefinition> GetBuiltins() => Builtins;

        private static TemplateDefinition? FindTemplate(
            string name, Dictionary<string, TemplateDefinition> fileCache)
        {
            if (fileCache.TryGetValue(name, out var f)) return f;
            if (Builtins.TryGetValue(name, out var b)) return b;
            return null;
        }

        private static Dictionary<string, TemplateDefinition> LoadFileTemplates(
            string dir, List<string>? warnings)
        {
            var cache = new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return cache;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var tpl = JsonSerializer.Deserialize<TemplateDefinition>(File.ReadAllText(file), Json.Pretty);
                    if (tpl == null || string.IsNullOrWhiteSpace(tpl.Name)) continue;
                    cache[tpl.Name] = tpl;
                }
                catch (Exception ex)
                {
                    warnings?.Add($"Could not load template file '{file}': {ex.Message}");
                }
            }
            return cache;
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 5 — CONFIG VALIDATOR
    // ═══════════════════════════════════════════════════════════════════════════

    public static class ConfigValidator
    {
        public class ValidationResult
        {
            public bool Valid { get; set; } = true;
            public List<string> Errors { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        private static readonly HashSet<string> BuiltinTags = new(StringComparer.Ordinal)
        {
            "Untagged","Respawn","Finish","EditorOnly","MainCamera","Player","GameController",
        };

        private static readonly HashSet<string> BuiltinLayers = new(StringComparer.Ordinal)
        {
            "Default","TransparentFX","Ignore Raycast","Water","UI",
        };

        /// <summary>
        /// Validates a fully-loaded, template-resolved SceneGenConfig.
        /// Called by GenerationEngine after ZipLoader + TemplateResolver.
        /// </summary>
        public static ValidationResult ValidateConfig(SceneGenConfig cfg)
        {
            var r = new ValidationResult();

            // ── 1. Project ────────────────────────────────────────────────
            if (cfg.Project == null)
                r.Warnings.Add("No 'project' block — defaults will be used (name: MyProject, unityVersion: 2022.3.20f1).");
            else if (string.IsNullOrWhiteSpace(cfg.Project.Name))
                r.Errors.Add("'project.name' is required.");

            // ── 2. Scenes ─────────────────────────────────────────────────
            if (cfg.Scenes.Count == 0)
                r.Errors.Add("'scenes' must contain at least one entry.");

            foreach (var scene in cfg.Scenes)
            {
                if (string.IsNullOrWhiteSpace(scene.Name))
                    r.Errors.Add("A scene entry is missing 'name'.");
                if (string.IsNullOrWhiteSpace(scene.Path))
                    r.Errors.Add($"Scene '{scene.Name}': missing 'path' (e.g. \"Assets/Scenes/{scene.Name}.unity\").");
                if (scene.Roots.Count == 0)
                    r.Warnings.Add($"Scene '{scene.Name}' has no root GameObjects — the scene will be empty.");
            }

            // ── 3. GameObject IDs ─────────────────────────────────────────
            var goIds = new HashSet<string>();
            foreach (var go in cfg.GameObjects)
            {
                if (string.IsNullOrWhiteSpace(go.Id))
                    r.Errors.Add($"A GameObject (name: '{go.Name}') has no 'id'.");
                else if (!goIds.Add(go.Id))
                    r.Errors.Add($"Duplicate GameObject id: '{go.Id}'.");

                if (string.IsNullOrWhiteSpace(go.Name))
                    r.Errors.Add($"GameObject '{go.Id}' has no 'name'.");
            }

            // ── 4. Dangling references ────────────────────────────────────
            foreach (var go in cfg.GameObjects)
                foreach (var childId in go.Children)
                    if (!goIds.Contains(childId))
                        r.Errors.Add($"'{go.Id}' has child '{childId}' but no GameObject with that id exists.");

            foreach (var scene in cfg.Scenes)
                foreach (var rootId in scene.Roots)
                    if (!goIds.Contains(rootId))
                        r.Errors.Add($"Scene '{scene.Name}' lists root '{rootId}' but it doesn't exist.");

            // ── 5. Cycle detection ────────────────────────────────────────
            if (TryFindCycle(cfg, out var cyclePath))
                r.Errors.Add($"Cycle in GameObject hierarchy: {cyclePath}.");

            // ── 6. ID convention warnings ────────────────────────────────
            var byId = cfg.GameObjects.ToDictionary(g => g.Id);
            foreach (var go in cfg.GameObjects)
                foreach (var childId in go.Children)
                {
                    if (!goIds.Contains(childId)) continue;
                    if (!childId.StartsWith(go.Id + ".", StringComparison.Ordinal))
                    {
                        var suggested = go.Id + "." +
                            (byId.TryGetValue(childId, out var ch)
                                ? ch.Name.ToLowerInvariant().Replace(' ', '_')
                                : childId);
                        r.Warnings.Add($"ID convention: '{childId}' is a child of '{go.Id}' " +
                                       $"but doesn't start with '{go.Id}.'. Suggested: '{suggested}'.");
                    }
                }

            // ── 7. Unresolved templates ───────────────────────────────────
            foreach (var go in cfg.GameObjects)
                if (!string.IsNullOrWhiteSpace(go.Template))
                    r.Errors.Add($"'{go.Id}' still has unresolved template '{go.Template}'. " +
                                 $"Built-ins: {string.Join(", ", TemplateResolver.GetBuiltins().Keys)}.");

            // ── 8. Component types ────────────────────────────────────────
            var scriptNames = new HashSet<string>(
                cfg.Scripts.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var go in cfg.GameObjects)
                foreach (var comp in go.Components)
                {
                    if (string.IsNullOrWhiteSpace(comp.Type))
                        r.Errors.Add($"A component on '{go.Id}' has no 'type'.");
                    if (comp.Props != null)
                        ValidateProps(comp.Props, go.Id, comp.Type, goIds, r);
                }

            // ── 9. Tags and layers ────────────────────────────────────────
            var declaredTags = cfg.Settings?.Tags != null
                ? new HashSet<string>(cfg.Settings.Tags, StringComparer.Ordinal) : new();
            var declaredLayers = cfg.Settings?.Layers != null
                ? new HashSet<string>(cfg.Settings.Layers, StringComparer.Ordinal) : new();

            foreach (var go in cfg.GameObjects)
            {
                if (!string.IsNullOrEmpty(go.Tag)
                    && go.Tag != "Untagged"
                    && !BuiltinTags.Contains(go.Tag)
                    && !declaredTags.Contains(go.Tag))
                    r.Errors.Add($"'{go.Id}' uses tag '{go.Tag}' not declared in settings.tags.");

                if (!string.IsNullOrEmpty(go.Layer)
                    && go.Layer != "Default"
                    && !BuiltinLayers.Contains(go.Layer)
                    && !declaredLayers.Contains(go.Layer))
                    r.Errors.Add($"'{go.Id}' uses layer '{go.Layer}' not declared in settings.layers.");
            }

            // ── 10. Script names ──────────────────────────────────────────
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in cfg.Scripts)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                { r.Errors.Add("A discovered script has an empty name."); continue; }

                if (!Regex.IsMatch(s.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    r.Errors.Add($"Script name '{s.Name}' is not a valid C# identifier.");

                if (!seenNames.Add(s.Name))
                    r.Errors.Add($"Duplicate script name: '{s.Name}'.");
            }

            // ── 11. Warn on component types that look custom but aren't in scripts/ ──
            if (scriptNames.Count > 0)
                foreach (var go in cfg.GameObjects)
                    foreach (var comp in go.Components)
                    {
                        var t = comp.Type ?? "";
                        if (!t.Contains('.') && !scriptNames.Contains(t))
                            r.Warnings.Add(
                                $"Component type '{t}' on '{go.Id}' has no namespace and is not in scripts/. " +
                                $"If it's a custom MonoBehaviour, add {t}.cs to the scripts/ folder in your zip.");
                    }

            r.Valid = r.Errors.Count == 0;
            return r;
        }

        private static void ValidateProps(
            Dictionary<string, object> props,
            string goId, string compType,
            HashSet<string> goIds,
            ValidationResult r)
        {
            foreach (var (key, rawValue) in props)
            {
                if (rawValue == null) continue;

                // With System.Text.Json, Dictionary<string,object> values are JsonElement.
                // Built-in template props may be plain CLR types (string, double[], bool) —
                // handle both cases.
                string? strVal = rawValue is string s ? s
                    : rawValue is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString()
                    : null;

                int? arrayLen = rawValue is JsonElement ja && ja.ValueKind == JsonValueKind.Array ? ja.GetArrayLength()
                    : rawValue is System.Collections.IList list ? list.Count
                    : (int?)null;

                bool isNumericArray = false;
                if (arrayLen.HasValue)
                {
                    isNumericArray = true;
                    if (rawValue is JsonElement jArr)
                    {
                        foreach (var el in jArr.EnumerateArray())
                            if (el.ValueKind != JsonValueKind.Number)
                            { isNumericArray = false; break; }
                    }
                    // CLR arrays from built-in templates are always numeric
                }

                if (strVal != null)
                {
                    if (strVal.StartsWith('#') && !IsValidColor(strVal))
                        r.Errors.Add($"'{compType}' on '{goId}': '{key}' = \"{strVal}\" " +
                                     $"is not a valid color. Use #RRGGBBAA (8 hex digits).");

                    if (strVal.StartsWith("ref:", StringComparison.Ordinal))
                    {
                        var refId = strVal[4..];
                        if (string.IsNullOrWhiteSpace(refId))
                            r.Errors.Add($"'{compType}' on '{goId}': '{key}' is \"ref:\" with no id.");
                        else if (!goIds.Contains(refId))
                            r.Errors.Add($"'{compType}' on '{goId}': '{key}' references '{refId}' which doesn't exist.");
                    }
                }
                else if (arrayLen.HasValue)
                {
                    if (arrayLen.Value != 2 && arrayLen.Value != 3 && arrayLen.Value != 4)
                        r.Errors.Add($"'{compType}' on '{goId}': '{key}' array has {arrayLen.Value} elements " +
                                     $"(must be 2=Vector2, 3=Vector3, or 4=Vector4).");

                    if (!isNumericArray)
                        r.Errors.Add($"'{compType}' on '{goId}': '{key}' array contains non-numeric elements.");
                }
            }
        }

        private static bool IsValidColor(string s)
        {
            if (s.Length != 9) return false;
            foreach (var c in s.AsSpan(1))
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static bool TryFindCycle(SceneGenConfig cfg, out string cyclePath)
        {
            cyclePath = "";
            var byId = cfg.GameObjects.ToDictionary(g => g.Id);
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();
            var stack = new List<string>();
            string found = "";

            bool Dfs(string id)
            {
                if (inStack.Contains(id)) { stack.Add(id); found = string.Join(" → ", stack); return true; }
                if (visited.Contains(id)) return false;
                visited.Add(id); inStack.Add(id); stack.Add(id);
                if (byId.TryGetValue(id, out var go))
                    foreach (var child in go.Children)
                        if (Dfs(child)) return true;
                stack.RemoveAt(stack.Count - 1);
                inStack.Remove(id);
                return false;
            }

            foreach (var go in cfg.GameObjects)
                if (Dfs(go.Id)) { cyclePath = found; return true; }

            return false;
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 6 — PROJECT CREATOR
    // ═══════════════════════════════════════════════════════════════════════════

    public static class ProjectCreator
    {
        /// <summary>
        /// Creates or verifies the Unity project folder and writes all generated files.
        /// Scripts are copied directly from their absolute paths (set by ZipLoader).
        /// </summary>
        public static string CreateOrVerify(
            SceneGenConfig cfg,
            string outputDir,
            bool force,
            Action<string> log)
        {
            var projectName = cfg.Project?.Name ?? "MyProject";
            var unityVersion = cfg.Project?.UnityVersion ?? "2022.3.20f1";
            var projectRoot = Path.Combine(outputDir, projectName);

            bool exists = Directory.Exists(projectRoot)
                       && Directory.Exists(Path.Combine(projectRoot, "Assets"))
                       && Directory.Exists(Path.Combine(projectRoot, "ProjectSettings"));

            if (exists && force)
            {
                log($"[ProjectCreator] --force: removing {projectRoot}");
                Directory.Delete(projectRoot, recursive: true);
                exists = false;
            }

            if (!exists)
            {
                log($"[ProjectCreator] Creating project at {projectRoot}");
                Scaffold(projectRoot, cfg, unityVersion, log);
            }
            else
            {
                log($"[ProjectCreator] Reusing existing project at {projectRoot}");
            }

            // Always refresh these so changes take effect on re-runs
            WriteBuilderScript(projectRoot, log);
            WriteScripts(cfg, projectRoot, log);
            WriteConfig(cfg, projectRoot, log);

            return projectRoot;
        }

        // ── Scaffold ──────────────────────────────────────────────────────

        private static void Scaffold(
            string root, SceneGenConfig cfg, string unityVersion, Action<string> log)
        {
            var dirs = new[]
            {
                "Assets", "Assets/Scenes", "Assets/Scripts",
                "Assets/Editor", "Assets/Editor/SceneGenerator",
                "Packages", "ProjectSettings", "Logs",
            };

            foreach (var d in dirs)
            {
                Directory.CreateDirectory(
                    Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
                log($"  mkdir {d}");
            }

            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                $"m_EditorVersion: {unityVersion}\n");

            WritePackageManifest(root, cfg, log);
        }

        // All 31 built-in Unity modules — used when no packages declared
        private static readonly Dictionary<string, string> AllBuiltinModules =
            new Dictionary<string, string>
            {
                ["com.unity.modules.accessibility"] = "1.0.0",
                ["com.unity.modules.ai"] = "1.0.0",
                ["com.unity.modules.androidjni"] = "1.0.0",
                ["com.unity.modules.animation"] = "1.0.0",
                ["com.unity.modules.assetbundle"] = "1.0.0",
                ["com.unity.modules.audio"] = "1.0.0",
                ["com.unity.modules.cloth"] = "1.0.0",
                ["com.unity.modules.director"] = "1.0.0",
                ["com.unity.modules.imageconversion"] = "1.0.0",
                ["com.unity.modules.imgui"] = "1.0.0",
                ["com.unity.modules.jsonserialize"] = "1.0.0",
                ["com.unity.modules.particlesystem"] = "1.0.0",
                ["com.unity.modules.physics"] = "1.0.0",
                ["com.unity.modules.physics2d"] = "1.0.0",
                ["com.unity.modules.screencapture"] = "1.0.0",
                ["com.unity.modules.terrain"] = "1.0.0",
                ["com.unity.modules.terrainphysics"] = "1.0.0",
                ["com.unity.modules.tilemap"] = "1.0.0",
                ["com.unity.modules.ui"] = "1.0.0",
                ["com.unity.modules.uielements"] = "1.0.0",
                ["com.unity.modules.umbra"] = "1.0.0",
                ["com.unity.modules.unityanalytics"] = "1.0.0",
                ["com.unity.modules.unitywebrequest"] = "1.0.0",
                ["com.unity.modules.unitywebrequestassetbundle"] = "1.0.0",
                ["com.unity.modules.unitywebrequestaudio"] = "1.0.0",
                ["com.unity.modules.unitywebrequesttexture"] = "1.0.0",
                ["com.unity.modules.unitywebrequestwww"] = "1.0.0",
                ["com.unity.modules.vehicles"] = "1.0.0",
                ["com.unity.modules.video"] = "1.0.0",
                ["com.unity.modules.vr"] = "1.0.0",
                ["com.unity.modules.wind"] = "1.0.0",
                ["com.unity.modules.xr"] = "1.0.0",
            };

        private static void WritePackageManifest(
            string root, SceneGenConfig cfg, Action<string> log)
        {
            var userPackages = cfg.Project?.Packages ?? new List<string>();
            var declaredModules = new Dictionary<string, string>();
            var registryPackages = new Dictionary<string, string>();

            foreach (var pkg in userPackages)
            {
                var parts = pkg.Split('@');
                var name = parts[0].Trim();
                var version = parts.Length > 1 ? parts[1].Trim() : "1.0.0";

                if (name.StartsWith("com.unity.modules.", StringComparison.OrdinalIgnoreCase))
                    declaredModules[name] = version;
                else
                    registryPackages[name] = version;
            }

            var modules = declaredModules.Count > 0
                ? declaredModules
                : new Dictionary<string, string>(AllBuiltinModules);

            registryPackages.TryAdd("com.unity.ugui", "1.0.0");
            registryPackages.TryAdd("com.unity.nuget.newtonsoft-json", "3.2.1");

            var deps = new Dictionary<string, string>(modules);
            foreach (var kv in registryPackages) deps[kv.Key] = kv.Value;

            File.WriteAllText(
                Path.Combine(root, "Packages", "manifest.json"),
                JsonSerializer.Serialize(new { dependencies = deps }, Json.Pretty));

            log($"  Packages/manifest.json — {deps.Count} packages");
        }

        // ── Scripts ───────────────────────────────────────────────────────
        // Scripts come exclusively from the scripts/ folder in the zip.
        // Each ScriptEntry.AbsolutePath points to the extracted temp file.
        // We copy it directly — no wrapping, no generation.

        private static void WriteScripts(SceneGenConfig cfg, string root, Action<string> log)
        {
            if (cfg.Scripts.Count == 0) return;

            var dir = Path.Combine(root, "Assets", "Scripts");
            Directory.CreateDirectory(dir);

            foreach (var script in cfg.Scripts)
            {
                if (!File.Exists(script.AbsolutePath))
                {
                    log($"[ProjectCreator] ⚠ Script file not found, skipping: {script.AbsolutePath}");
                    continue;
                }

                var dest = Path.Combine(dir, $"{script.Name}.cs");
                File.Copy(script.AbsolutePath, dest, overwrite: true);
                log($"[ProjectCreator] Script → {dest}");
            }
        }

        // ── Config ────────────────────────────────────────────────────────
        // Writes the template-resolved SceneGenConfig as SceneGenConfig.json
        // in the project root. Unity's Builder.cs reads this.
        // Note: Scripts are [JsonIgnore] so they don't appear in this file.

        private static void WriteConfig(SceneGenConfig cfg, string root, Action<string> log)
        {
            var dest = Path.Combine(root, "SceneGenConfig.json");
            File.WriteAllText(dest, JsonSerializer.Serialize(cfg, Json.Pretty));
            log($"[ProjectCreator] SceneGenConfig.json → {dest}");
        }

        // ── Builder script ────────────────────────────────────────────────

        private static void WriteBuilderScript(string root, Action<string> log)
        {
            var dir = Path.Combine(root, "Assets", "Editor", "SceneGenerator");
            Directory.CreateDirectory(dir);

            var src = ExtractBuilderSource();
            var dest = Path.Combine(dir, "Builder.cs");
            File.WriteAllText(dest, src);
            log($"[ProjectCreator] Builder.cs → {dest}");
        }

        private static string ExtractBuilderSource()
        {
            const string resource = "UnitySceneGen.UnityBuilder.Builder.cs";
            var asm = Assembly.GetExecutingAssembly();

            using var stream = asm.GetManifestResourceStream(resource);
            if (stream != null)
                using (var sr = new StreamReader(stream))
                    return sr.ReadToEnd();

            var adjacent = Path.Combine(Path.GetDirectoryName(asm.Location)!, "Builder.cs");
            if (File.Exists(adjacent))
                return File.ReadAllText(adjacent);

            throw new FileNotFoundException(
                $"Builder script not found. Expected embedded resource '{resource}' " +
                $"or Builder.cs next to the executable.");
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 7 — UNITY LAUNCHER
    // ═══════════════════════════════════════════════════════════════════════════

    public static class UnityLauncher
    {
        private static readonly TimeSpan Pass1Timeout = TimeSpan.FromMinutes(12);
        private static readonly TimeSpan Pass2Timeout = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan HangDetectWindow = TimeSpan.FromSeconds(120);

        private const string ResultFile = "SceneGenResult.json";
        private const string RunningFile = "SceneGenRunning.json";

        // ── Pass 1 — Import packages + compile ───────────────────────────

        public static async Task<(bool ok, string error)> Pass1ImportAsync(
            string unityExe, string projectPath,
            Action<string> log, CancellationToken ct)
        {
            log("[Unity Pass 1] Starting — package import & script compilation…");
            var logFile = Path.Combine(projectPath, "SceneGenUnity_Pass1.log");
            var args = BuildArgs(projectPath, logFile, executeMethod: null);

            // Unity 6 exits with code 1 even on success.
            // Pass 1 success = absence of failure patterns, not presence of success string.
            return await RunUnityAsync(unityExe, args, logFile,
                Pass1Timeout, log, ct,
                successPattern: null,
                failurePatterns: new[] {
                    "compilation errors", "Error building Player",
                    "Failed to compile", "Scripts have compile errors", "error CS" });
        }

        // ── Pass 2 — Run scene builder ────────────────────────────────────

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

            var resultPath = Path.Combine(projectPath, ResultFile);
            if (!File.Exists(resultPath))
            {
                return ok
                    ? Fail($"Unity exited cleanly but Builder never wrote its result file. Check log: {logFile}")
                    : Fail($"Unity failed before Builder.Run executed. {error}  Check log: {logFile}");
            }

            try
            {
                var result = JsonSerializer.Deserialize<BuilderResult>(File.ReadAllText(resultPath), Json.Pretty);
                return result ?? Fail("SceneGenResult.json was empty or malformed.");
            }
            catch (Exception ex)
            {
                return Fail($"Could not parse SceneGenResult.json: {ex.Message}");
            }
        }

        // ── Core process runner ───────────────────────────────────────────

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

            // ── Log tail task ─────────────────────────────────────────────
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

            // ── Wait loop ─────────────────────────────────────────────────
            var deadline = DateTime.UtcNow + timeout;
            bool timedOut = false, hangKill = false;

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
                    log($"[ERROR] UNITY TIMEOUT AFTER {timeout.TotalMinutes:F0} MIN — KILLING PROCESS.");
                    KillSafe(proc);
                    break;
                }

                if (DateTime.UtcNow - lastActivity > HangDetectWindow)
                {
                    hangKill = true;
                    log($"[ERROR] UNITY PRODUCED NO LOG OUTPUT FOR {HangDetectWindow.TotalSeconds:F0}S — POSSIBLE LICENSE ISSUE OR HANG. KILLING PROCESS.");
                    KillSafe(proc);
                    break;
                }

                await Task.Delay(1000);
            }

            tailCts.Cancel();
            await Task.Delay(600);

            int exitCode = (timedOut || hangKill) ? -1 : proc.ExitCode;
            log($"[Unity] Process ended — exit code {exitCode}");

            if (timedOut) return (false, $"Unity timed out after {timeout.TotalMinutes:F0} min.");
            if (hangKill) return (false, "Unity stopped producing output (license/hang).");

            if (File.Exists(logFile))
            {
                var content = ReadSafe(logFile);
                if (failurePatterns != null)
                    foreach (var pat in failurePatterns)
                        if (content.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            log($"[ERROR] UNITY LOG CONTAINS FAILURE PATTERN: '{pat.ToUpperInvariant()}'");
                            return (false, $"Unity log contains failure: '{pat}'");
                        }

                if (successPattern != null
                    && exitCode != 0
                    && content.IndexOf(successPattern, StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, $"Unity exited {exitCode} without success pattern.");
            }

            bool hasSuccess = successPattern == null
                || (File.Exists(logFile) && ReadSafe(logFile)
                        .IndexOf(successPattern, StringComparison.OrdinalIgnoreCase) >= 0);
            bool clean = (exitCode == 0 || exitCode == 1) && hasSuccess;
            return (clean, clean ? "" : $"Unity exited with code {exitCode}");
        }

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
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string ReadSafe(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static BuilderResult Fail(string error) =>
            new BuilderResult { Success = false, Error = error };
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 8 — GENERATION ENGINE
    //
    // Orchestrates the full pipeline:
    //   1. Extract zip + load scene.json + discover scripts
    //   2. Resolve templates
    //   3. Validate config
    //   4. Scaffold Unity project folder
    //   5. Unity Pass 1 — package import + compile
    //   6. Unity Pass 2 — scene build
    // ═══════════════════════════════════════════════════════════════════════════

    public static class GenerationEngine
    {
        public static GenerationResult Run(
            byte[] zipBytes,
            string unityExePath,
            string outputDir,
            bool force,
            Action<string> log,
            CancellationToken ct)
        {
            string? extractDir = null;
            try
            {
                // ── 1. Load ───────────────────────────────────────────────
                log("=== Step 1/6: Loading scene zip ===");

                ZipLoader.LoadResult loaded;
                try
                {
                    loaded = ZipLoader.Load(zipBytes, log);
                    extractDir = loaded.ExtractDir;
                }
                catch (Exception ex)
                {
                    return Fail($"Zip load error: {ex.Message}");
                }

                var cfg = loaded.Config;

                // ── 2. Resolve templates ──────────────────────────────────
                log("=== Step 2/6: Resolving templates ===");

                var templateWarnings = new List<string>();
                TemplateResolver.Resolve(cfg, loaded.TemplatesDir, templateWarnings);
                foreach (var w in templateWarnings) log($"  [WARN] {w.ToUpperInvariant()}");
                ct.ThrowIfCancellationRequested();

                // ── 3. Validate ───────────────────────────────────────────
                log("=== Step 3/6: Validating config ===");

                var validation = ConfigValidator.ValidateConfig(cfg);
                foreach (var w in validation.Warnings) log($"  [WARN] {w.ToUpperInvariant()}");

                if (!validation.Valid)
                {
                    foreach (var e in validation.Errors) log($"  [ERROR] {e.ToUpperInvariant()}");
                    log($"[ERROR] CONFIG VALIDATION FAILED — {validation.Errors.Count} ERROR(S): {string.Join("; ", validation.Errors).ToUpperInvariant()}");
                    return Fail(
                        $"Config validation failed ({validation.Errors.Count} error(s)): " +
                        string.Join("; ", validation.Errors));
                }
                log("  ✓  Config valid.");
                ct.ThrowIfCancellationRequested();

                // ── 4. Scaffold project ───────────────────────────────────
                log("=== Step 4/6: Setting up project folder ===");

                string projectPath;
                try
                {
                    projectPath = ProjectCreator.CreateOrVerify(cfg, outputDir, force, log);
                }
                catch (Exception ex)
                {
                    log($"[ERROR] PROJECT CREATION FAILED: {ex.Message.ToUpperInvariant()}");
                    return Fail($"Project creation error: {ex.Message}");
                }
                log($"  ✓  Project folder ready: {projectPath}");
                ct.ThrowIfCancellationRequested();

                // ── 5. Unity Pass 1 ───────────────────────────────────────
                log("=== Step 5/6: Unity Pass 1 — Package import & compile ===");

                var pass1 = UnityLauncher.Pass1ImportAsync(
                    unityExePath, projectPath, log, ct).GetAwaiter().GetResult();

                if (!pass1.ok)
                {
                    log($"[ERROR] UNITY PASS 1 FAILED: {pass1.error.ToUpperInvariant()}");
                    return Fail($"Unity Pass 1 failed: {pass1.error}");
                }

                log("  ✓  Pass 1 complete.");
                ct.ThrowIfCancellationRequested();

                // ── 6. Unity Pass 2 ───────────────────────────────────────
                log("=== Step 6/6: Unity Pass 2 — Scene generation ===");

                BuilderResult builderResult;
                try
                {
                    builderResult = UnityLauncher.Pass2BuildAsync(
                        unityExePath, projectPath, log, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log($"[ERROR] UNITY PASS 2 LAUNCH ERROR: {ex.Message.ToUpperInvariant()}");
                    return Fail($"Unity Pass 2 launch error: {ex.Message}");
                }

                foreach (var w in builderResult.Warnings) log($"  [WARN] {w.ToUpperInvariant()}");

                if (!builderResult.Success)
                {
                    log($"[ERROR] UNITY BUILDER FAILED: {builderResult.Error.ToUpperInvariant()}");
                    return Fail($"Unity Builder failed: {builderResult.Error}");
                }

                log("  ✓  Scene(s) generated.");

                var summaryPath = Path.Combine(projectPath, "SceneGenOutput.json");
                File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
                {
                    projectPath,
                    scenes = builderResult.Scenes,
                    warnings = builderResult.Warnings,
                    generatedAt = DateTime.UtcNow.ToString("o"),
                }, Json.Pretty));

                return new GenerationResult
                {
                    Success = true,
                    ProjectPath = projectPath,
                    SceneGenOutputPath = summaryPath,
                    Warnings = builderResult.Warnings,
                };
            }
            finally
            {
                if (extractDir != null)
                    try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Extract, resolve templates, and validate — without running Unity.
        /// Used by POST /validate. Fast (&lt; 1 s).
        /// </summary>
        public static ConfigValidator.ValidationResult ValidateZip(byte[] zipBytes)
        {
            string? extractDir = null;
            try
            {
                ZipLoader.LoadResult loaded;
                try
                {
                    loaded = ZipLoader.Load(zipBytes);
                    extractDir = loaded.ExtractDir;
                }
                catch (Exception ex)
                {
                    var r = new ConfigValidator.ValidationResult();
                    r.Errors.Add(ex.Message);
                    r.Valid = false;
                    return r;
                }

                var templateWarnings = new List<string>();
                TemplateResolver.Resolve(loaded.Config, loaded.TemplatesDir, templateWarnings);

                var result = ConfigValidator.ValidateConfig(loaded.Config);
                result.Warnings.InsertRange(0, templateWarnings);
                return result;
            }
            finally
            {
                if (extractDir != null)
                    try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        private static GenerationResult Fail(string error) =>
            new GenerationResult { Success = false, Error = error };
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 9 — COMPONENT SCHEMA
    // Returned by GET /schema — machine-readable catalog of all components,
    // templates, prop formats, and the zip contract.
    // ═══════════════════════════════════════════════════════════════════════════

    public static class ComponentSchema
    {
        public static object Build() => new
        {
            version = "2.0",

            usage = new
            {
                summary = "Fetch GET /schema once per session before generating configs.",
                workflow = new[]
                {
                    "1. GET /schema       — learn what is available (this endpoint)",
                    "2. Build scene.zip   — scene.json + scripts/ folder",
                    "3. POST /validate    — fix errors cheaply (< 1 s, no Unity needed)",
                    "4. POST /generate    — run the full pipeline (5–20 min)",
                    "5. GET  /status      — poll progress while /generate is running",
                },
            },

            // ── Input contract ────────────────────────────────────────────
            inputContract = new
            {
                summary = "The only valid input is a base64-encoded scene.zip sent to POST /generate or POST /validate.",

                zipStructure = new[]
                {
                    "scene.zip",
                    "├── scene.json          ← required: project, settings, scenes, gameObjects",
                    "├── scripts/            ← optional: .cs files, auto-discovered, copied as-is",
                    "│   ├── PlayerController.cs",
                    "│   └── EnemyAI.cs",
                    "└── templates/          ← optional: .json custom template files",
                },

                rules = new[]
                {
                    "scene.json is WIRING ONLY — project config, scenes, GameObjects, components.",
                    "scene.json NEVER contains C# code.",
                    "Scripts are ALWAYS physical .cs files in the scripts/ folder.",
                    "All .cs files in scripts/ are auto-discovered — no declaration needed in scene.json.",
                    "The class name is the filename without extension: PlayerController.cs → type: \"PlayerController\".",
                    "Scripts must be complete, valid C# MonoBehaviour files.",
                    "templates/ contains .json files for custom component bundles.",
                },

                sceneJsonFields = new
                {
                    project = "{ name, unityVersion, packages[] }",
                    settings = "{ tags[], layers[] }",
                    scenes = "[ { name, path, roots[] } ]",
                    gameObjects = "[ { id, name, tag, layer, active, children[], components[], template, templateProps } ]",
                },

                example = new
                {
                    project = new { name = "MyProject", unityVersion = "2022.3.20f1" },
                    settings = new { tags = new[] { "Player" }, layers = new[] { "Gameplay" } },
                    scenes = new[] { new { name = "Main", path = "Assets/Scenes/Main.unity", roots = new[] { "go.root" } } },
                    gameObjects = new object[]
                    {
                        new { id = "go.root",             name = "Root",             children = new[] { "go.root.camera", "go.root.light" } },
                        new { id = "go.root.camera",      name = "Main Camera",      tag = "MainCamera", template = "camera/main" },
                        new { id = "go.root.light",       name = "Directional Light", template = "light/directional",
                              components = new[] { new { type = "UnityEngine.Transform", props = new { localEulerAngles = new[] { 50, -30, 0 } } } } },
                    },
                },
            },

            // ── Prop value formats ────────────────────────────────────────
            propFormats = new
            {
                Color = "#RRGGBBAA — exactly 8 hex digits, alpha required. Examples: \"#FFFFFFFF\", \"#FF000080\".",
                Vector2 = "[x, y]",
                Vector3 = "[x, y, z]",
                Vector4 = "[x, y, z, w]",
                @ref = "\"ref:<gameObjectId>\" — e.g. \"ref:go.camera\"",
                @enum = "Plain string matching the enum value exactly (case-sensitive).",
                @float = "JSON number.",
                @int = "JSON integer.",
                @bool = "true or false.",
                @string = "JSON string.",
            },

            // ── ID naming convention ──────────────────────────────────────
            idConvention = new
            {
                rule = "Child id must start with parent id + '.'. Hierarchy is self-documenting from ids alone.",
                examples = new[]
                {
                    "go.root",
                    "go.root.camera       ← child of go.root",
                    "go.root.light        ← child of go.root",
                    "go.ui",
                    "go.ui.panel          ← child of go.ui",
                    "go.ui.panel.title    ← child of go.ui.panel",
                },
            },

            builtinTags = new[] { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" },
            builtinLayers = new[] { "Default", "TransparentFX", "Ignore Raycast", "Water", "UI" },

            // ── Template catalog ──────────────────────────────────────────
            templates = new
            {
                usage = new[]
                {
                    "Add \"template\": \"<name>\" to any GameObject.",
                    "Override props with \"templateProps\": { \"<friendlyKey>\": <value> }.",
                    "Explicit \"components\" merge on top — explicit always wins over template defaults.",
                    "After resolution template field is cleared; validator sees only expanded components.",
                    "File templates: place .json files in templates/ folder next to scene.json.",
                },
                builtins = TemplateResolver.GetBuiltins().ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        description = kv.Value.Description,
                        propMappings = kv.Value.PropMappings,
                        componentTypes = kv.Value.Components.Select(c => c.Type).ToArray(),
                    }),
            },

            // ── Component catalog ─────────────────────────────────────────
            components = new Dictionary<string, object>
            {
                ["UnityEngine.Transform"] = Comp(
                    notes: new[] {
                        "Every GameObject already has a Transform. Do NOT add it with AddComponent.",
                        "List it only to set position, rotation, or scale via props.",
                        "All values are local-space (relative to parent).",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["localPosition"] = P("Vector3", def: "[0, 0, 0]"),
                        ["localEulerAngles"] = P("Vector3", def: "[0, 0, 0]", notes: "Rotation in degrees."),
                        ["localScale"] = P("Vector3", def: "[1, 1, 1]"),
                    }),

                ["UnityEngine.Camera"] = Comp(
                    notes: new[] {
                        "Pair with UnityEngine.AudioListener on the same GameObject.",
                        "Exactly one camera should have tag 'MainCamera'.",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["clearFlags"] = P("enum", def: "Skybox", vals: new[] { "Skybox", "SolidColor", "DepthOnly", "Nothing" }),
                        ["backgroundColor"] = P("Color", def: "#19191AFF", notes: "Only visible when clearFlags = SolidColor."),
                        ["fieldOfView"] = P("float", def: "60", range: new[] { 1, 179 }),
                        ["nearClipPlane"] = P("float", def: "0.3"),
                        ["farClipPlane"] = P("float", def: "1000"),
                        ["orthographic"] = P("bool", def: "false"),
                        ["depth"] = P("float", def: "-1", notes: "Higher draws on top."),
                    }),

                ["UnityEngine.AudioListener"] = Comp(
                    notes: new[] { "No props. One per scene." }),

                ["UnityEngine.Light"] = Comp(
                    notes: new[] { "Use localEulerAngles [50, -30, 0] on Transform for a natural sun angle." },
                    props: new Dictionary<string, object>
                    {
                        ["type"] = P("enum", def: "Directional", vals: new[] { "Directional", "Point", "Spot", "Area" }),
                        ["color"] = P("Color", def: "#FFFFFFFF"),
                        ["intensity"] = P("float", def: "1", range: new[] { 0, 8 }),
                        ["shadows"] = P("enum", def: "None", vals: new[] { "None", "Hard", "Soft" }),
                        ["range"] = P("float", def: "10", notes: "Point and Spot only."),
                        ["spotAngle"] = P("float", def: "30", notes: "Spot only.", range: new[] { 1, 179 }),
                    }),

                ["UnityEngine.Rigidbody"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["mass"] = P("float", def: "1"),
                        ["drag"] = P("float", def: "0"),
                        ["useGravity"] = P("bool", def: "true"),
                        ["isKinematic"] = P("bool", def: "false"),
                        ["collisionDetectionMode"] = P("enum", def: "Discrete",
                            vals: new[] { "Discrete", "Continuous", "ContinuousDynamic", "ContinuousSpeculative" },
                            notes: "Use ContinuousDynamic for fast-moving objects."),
                    }),

                ["UnityEngine.BoxCollider"] = Comp(props: ColliderProps(hasSize: true)),
                ["UnityEngine.SphereCollider"] = Comp(props: ColliderProps(hasRadius: true)),
                ["UnityEngine.CapsuleCollider"] = Comp(props: ColliderProps(hasCapsule: true)),
                ["UnityEngine.MeshCollider"] = Comp(
                    notes: new[] { "convex must be true when used with a non-kinematic Rigidbody." },
                    props: new Dictionary<string, object>
                    {
                        ["isTrigger"] = P("bool", def: "false"),
                        ["convex"] = P("bool", def: "false"),
                    }),

                ["UnityEngine.Canvas"] = Comp(
                    notes: new[] {
                        "MUST be the first component — it promotes Transform to RectTransform.",
                        "Pair with CanvasScaler and GraphicRaycaster on the same GameObject.",
                    },
                    props: new Dictionary<string, object>
                    {
                        ["renderMode"] = P("enum", def: "ScreenSpaceOverlay",
                            vals: new[] { "ScreenSpaceOverlay", "ScreenSpaceCamera", "WorldSpace" }),
                        ["sortingOrder"] = P("int", def: "0", notes: "Higher renders on top."),
                    }),

                ["UnityEngine.UI.CanvasScaler"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["uiScaleMode"] = P("enum", def: "ConstantPixelSize",
                            vals: new[] { "ConstantPixelSize", "ScaleWithScreenSize", "ConstantPhysicalSize" }),
                        ["referenceResolution"] = P("Vector2", def: "[1920, 1080]",
                            notes: "Only when uiScaleMode = ScaleWithScreenSize."),
                        ["matchWidthOrHeight"] = P("float", def: "0", range: new[] { 0, 1 },
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
                        ["anchorMin"] = P("Vector2", def: "[0.5, 0.5]", notes: "0=bottom/left, 1=top/right."),
                        ["anchorMax"] = P("Vector2", def: "[0.5, 0.5]"),
                        ["anchoredPosition"] = P("Vector2", def: "[0, 0]"),
                        ["sizeDelta"] = P("Vector2", def: "[100, 100]"),
                        ["offsetMin"] = P("Vector2", def: "[0, 0]"),
                        ["offsetMax"] = P("Vector2", def: "[0, 0]"),
                        ["pivot"] = P("Vector2", def: "[0.5, 0.5]", notes: "[0.5,0.5]=centre."),
                        ["localEulerAngles"] = P("Vector3", def: "[0, 0, 0]"),
                        ["localScale"] = P("Vector3", def: "[1, 1, 1]"),
                    }),

                ["UnityEngine.UI.Image"] = Comp(
                    props: new Dictionary<string, object>
                    {
                        ["color"] = P("Color", def: "#FFFFFFFF"),
                        ["raycastTarget"] = P("bool", def: "true", notes: "Set false on decorative images."),
                        ["fillAmount"] = P("float", def: "1", range: new[] { 0, 1 }),
                    }),

                ["UnityEngine.UI.Button"] = Comp(
                    notes: new[] { "Requires Image on same GameObject. Wire onClick via a MonoBehaviour in scripts/." },
                    props: new Dictionary<string, object>
                    {
                        ["interactable"] = P("bool", def: "true"),
                    }),

                ["TMPro.TextMeshProUGUI"] = Comp(
                    notes: new[] { "Preferred over UnityEngine.UI.Text for all new projects." },
                    props: new Dictionary<string, object>
                    {
                        ["text"] = P("string", def: ""),
                        ["fontSize"] = P("float", def: "36"),
                        ["color"] = P("Color", def: "#FFFFFFFF"),
                        ["alignment"] = P("enum", def: "TopLeft",
                            vals: new[] { "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight" }),
                        ["enableWordWrapping"] = P("bool", def: "true"),
                        ["overflowMode"] = P("enum", def: "Overflow",
                            vals: new[] { "Overflow", "Ellipsis", "Masking", "Truncate" }),
                        ["fontStyle"] = P("enum", def: "Normal",
                            vals: new[] { "Normal", "Bold", "Italic", "Underline", "Strikethrough" }),
                    }),

                ["UnityEngine.EventSystems.EventSystem"] = Comp(notes: new[] { "One per scene. Always pair with StandaloneInputModule." }),
                ["UnityEngine.EventSystems.StandaloneInputModule"] = Comp(notes: new[] { "Always pair with EventSystem." }),

                ["UnityEngine.AudioSource"] = Comp(
                    notes: new[] { "Assign clip at runtime via script. spatialBlend: 0=2D, 1=3D." },
                    props: new Dictionary<string, object>
                    {
                        ["volume"] = P("float", def: "1", range: new[] { 0, 1 }),
                        ["pitch"] = P("float", def: "1", range: new[] { -3, 3 }),
                        ["loop"] = P("bool", def: "false"),
                        ["playOnAwake"] = P("bool", def: "true"),
                        ["spatialBlend"] = P("float", def: "0", range: new[] { 0, 1 }),
                    }),

                ["UnityEngine.Animator"] = Comp(
                    notes: new[] { "Assign AnimatorController at runtime via script." },
                    props: new Dictionary<string, object>
                    {
                        ["applyRootMotion"] = P("bool", def: "false"),
                        ["updateMode"] = P("enum", def: "Normal",
                            vals: new[] { "Normal", "AnimatePhysics", "UnscaledTime" }),
                    }),
            },
        };

        private static object Comp(string[]? notes = null, Dictionary<string, object>? props = null)
            => new { notes = notes ?? Array.Empty<string>(), props = props ?? new Dictionary<string, object>() };

        private static object P(string type, string? def = null, int[]? range = null,
                                 string[]? vals = null, string? notes = null)
            => new { type, @default = def, range, values = vals, notes };

        private static Dictionary<string, object> ColliderProps(
            bool hasSize = false, bool hasRadius = false, bool hasCapsule = false)
        {
            var d = new Dictionary<string, object>
            {
                ["isTrigger"] = P("bool", def: "false"),
                ["center"] = P("Vector3", def: "[0, 0, 0]"),
            };
            if (hasSize) d["size"] = P("Vector3", def: "[1, 1, 1]");
            if (hasRadius) d["radius"] = P("float", def: "0.5");
            if (hasCapsule) { d["radius"] = P("float", def: "0.5"); d["height"] = P("float", def: "2"); }
            return d;
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 10 — API SERVER
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class ApiServer : IDisposable
    {
        /// <summary>Fixed port — always 46001. Cannot be overridden at runtime.</summary>
        public const int DefaultPort = 46001;

        private readonly int _port;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ProcessingGate _gate;

        /// <summary>
        /// When set, every log line produced during API processing is forwarded
        /// to the WPF log viewer so the user can follow API jobs in real time.
        /// </summary>
        public Action<string>? UiLog { get; set; }

        private readonly object _statusLock = new();
        private GenerationStatus _status = new();

        private void StatusLog(string line) { lock (_statusLock) { _status.Log.Add(line); } Console.WriteLine(line); }
        private void StatusStep(string step) { lock (_statusLock) { _status.Step = step; } }

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

        public ApiServer(int port = DefaultPort, ProcessingGate? gate = null)
        {
            _port = port;
            _gate = gate ?? new ProcessingGate();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
        }

        public void Start()
        {
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                try
                {
                    _listener.Start();
                }
                catch (System.Net.HttpListenerException ex)
                {
                    throw new InvalidOperationException(
                        $"[LAUNCH ERROR] Port {_port} is already in use. " +
                        $"Another process is listening on that port. " +
                        $"Stop the conflicting process and restart UnitySceneGen. " +
                        $"(HttpListenerException: {ex.Message})", ex);
                }
                Console.WriteLine($"[API] Listening  →  http://*:{_port}/");
                Console.WriteLine($"[API] Swagger UI →  http://*:{_port}/swagger");
                Console.WriteLine($"[API] OpenAPI    →  http://*:{_port}/openapi.json");
                _ = Task.Run(() => AcceptLoop(_cts.Token));
            }
        }

        public void Stop() { _cts.Cancel(); try { _listener.Stop(); } catch { } }
        public void Dispose() => Stop();

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

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

                switch (req.HttpMethod, path)
                {
                    case ("GET", "") or ("GET", "/"): await WriteJsonAsync(res, RootInfo()); break;
                    case ("GET", "/schema"): await WriteJsonAsync(res, ComponentSchema.Build()); break;
                    case ("GET", "/status"): await WriteJsonAsync(res, GetStatusSnapshot()); break;
                    case ("GET", "/swagger") or ("GET", "/swagger/"): await WriteRawAsync(res, SwaggerUiHtml(), "text/html"); break;
                    case ("GET", "/openapi.json"): await WriteRawAsync(res, OpenApiSpec(), "application/json"); break;
                    case ("GET", "/code"): await HandleGetCodeAsync(res); break;
                    case ("GET", "/csproj"): await HandleGetCsprojAsync(res); break;
                    case ("POST", "/validate"): await HandleValidateAsync(req, res); break;
                    case ("POST", "/generate"): await HandleGenerateAsync(req, res); break;
                    case ("POST", "/generate/upload"): await HandleGenerateUploadAsync(req, res); break;
                    case ("POST", "/build"): await HandleBuildAsync(req, res); break;
                    default:
                        res.StatusCode = 404;
                        await WriteJsonAsync(res, new { error = $"No route: {req.HttpMethod} {path}" });
                        break;
                }
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 500; await WriteJsonAsync(res, new { error = ex.Message }); } catch { }
            }
        }

        // ── POST /validate ────────────────────────────────────────────────

        private async Task HandleValidateAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await sr.ReadToEndAsync();

            string? zipBase64;
            try { zipBase64 = JsonDocument.Parse(body).RootElement.GetProperty("sceneZipBase64").GetString(); }
            catch (Exception ex) { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Bad JSON: {ex.Message}" }); return; }

            if (string.IsNullOrWhiteSpace(zipBase64))
            { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "sceneZipBase64 is required." }); return; }

            byte[] zipBytes;
            try { zipBytes = Convert.FromBase64String(zipBase64); }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "sceneZipBase64 is not valid base64." }); return; }

            var result = GenerationEngine.ValidateZip(zipBytes);
            res.StatusCode = result.Valid ? 200 : 422;
            await WriteJsonAsync(res, new { valid = result.Valid, errors = result.Errors, warnings = result.Warnings });
        }

        // ── POST /generate ────────────────────────────────────────────────
        // Now streams real-time logs via Server-Sent Events (SSE).
        // Response content-type: text/event-stream
        //
        // Event types:
        //   data: <log line>            — a single log line emitted as it happens
        //   event: result\ndata: ...    — job succeeded; JSON payload contains zipBase64
        //   event: error\ndata: ...     — job failed; JSON payload contains error + warnings
        //
        // Error and warning log lines are emitted in UPPER CASE for visibility.

        private async Task HandleGenerateAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            var _genToken = _gate.TryAcquire();
            if (_genToken == null)
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new { error = "Application is currently busy. Cannot process request.", hint = "Poll GET /status until 'running' is false, then retry." });
                return;
            }

            try
            {
                lock (_statusLock) _status = new GenerationStatus { Running = true, Step = "Starting…" };

                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                string? zipBase64, unityExeArg, outputDirArg;
                bool force;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    zipBase64 = root.TryGetProperty("sceneZipBase64", out var p1) ? p1.GetString() : null;
                    unityExeArg = root.TryGetProperty("unityExePath", out var p2) ? p2.GetString() : null;
                    outputDirArg = root.TryGetProperty("outputDir", out var p3) ? p3.GetString() : null;
                    force = root.TryGetProperty("force", out var p4) && p4.GetBoolean();
                }
                catch (Exception ex)
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Bad JSON: {ex.Message}" }); return; }

                if (string.IsNullOrWhiteSpace(zipBase64))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "sceneZipBase64 is required." }); return; }

                byte[] zipBytes;
                try { zipBytes = Convert.FromBase64String(zipBase64); }
                catch { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "sceneZipBase64 is not valid base64." }); return; }

                var unityExe = !string.IsNullOrWhiteSpace(unityExeArg)
                    ? unityExeArg : AppSettings.DefaultUnityExePath;

                if (!File.Exists(unityExe))
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = $"Unity executable not found: {unityExe}", defaultPath = AppSettings.DefaultUnityExePath });
                    return;
                }

                var outputDir = !string.IsNullOrWhiteSpace(outputDirArg)
                    ? outputDirArg
                    : Path.Combine(Path.GetTempPath(), $"UnitySceneGen_out_{Guid.NewGuid():N}");

                Directory.CreateDirectory(outputDir);

                // ── Open SSE response ─────────────────────────────────────
                res.StatusCode = 200;
                res.ContentType = "text/event-stream; charset=utf-8";
                res.AddHeader("Cache-Control", "no-cache");
                res.AddHeader("X-Accel-Buffering", "no");

                var logQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();

                void Log(string line)
                {
                    logQueue.Enqueue(line);
                    StatusLog(line);
                    UiLog?.Invoke(line);
                    if (line.Contains("Step 1/6")) StatusStep("1/6 — Loading zip");
                    else if (line.Contains("Step 2/6")) StatusStep("2/6 — Resolving templates");
                    else if (line.Contains("Step 3/6")) StatusStep("3/6 — Validating");
                    else if (line.Contains("Step 4/6")) StatusStep("4/6 — Setting up project");
                    else if (line.Contains("Step 5/6")) StatusStep("5/6 — Unity Pass 1 (compile)");
                    else if (line.Contains("Step 6/6")) StatusStep("6/6 — Unity Pass 2 (build)");
                }

                var genTask = Task.Run(() =>
                    GenerationEngine.Run(zipBytes, unityExe, outputDir, force, Log, CancellationToken.None));

                using var writer = new StreamWriter(res.OutputStream,
                    new System.Text.UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = false };

                while (!genTask.IsCompleted)
                {
                    await SseFlushLogs(writer, logQueue);
                    await Task.Delay(40);
                }
                await SseFlushLogs(writer, logQueue);  // drain final lines

                var result = await genTask;

                if (!result.Success)
                {
                    lock (_statusLock) { _status.Error = result.Error; _status.Step = "Failed"; }
                    var errPayload = JsonSerializer.Serialize(new { error = result.Error, warnings = result.Warnings }, Json.Compact);
                    await writer.WriteAsync($"event: error\ndata: {EscapeSseData(errPayload)}\n\n");
                    await writer.FlushAsync();
                    return;
                }

                StatusStep("Done — zipping result…");
                var projectName = Path.GetFileName(result.ProjectPath);
                var zipOutPath = Path.Combine(outputDir, $"{projectName}_output.zip");
                ZipFile.CreateFromDirectory(result.ProjectPath, zipOutPath,
                    CompressionLevel.Optimal, includeBaseDirectory: true);

                var zipOut = await File.ReadAllBytesAsync(zipOutPath);

                var donePayload = JsonSerializer.Serialize(new
                {
                    success = true,
                    projectName,
                    sizeKb = zipOut.Length / 1024,
                    warnings = result.Warnings,
                    zipBase64 = Convert.ToBase64String(zipOut),
                }, Json.Compact);

                await writer.WriteAsync($"event: result\ndata: {EscapeSseData(donePayload)}\n\n");
                await writer.FlushAsync();
                StatusLog($"[API] Done. Sent {zipOut.Length / 1024:N0} KB.");
            }
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                _genToken.Dispose();
                try { res.OutputStream.Close(); } catch { }
            }
        }

        // ── POST /build ───────────────────────────────────────────────────

        private async Task HandleBuildAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            var _buildToken = _gate.TryAcquire();
            if (_buildToken == null)
            { res.StatusCode = 503; await WriteJsonAsync(res, new { error = "Application is currently busy. Cannot process request." }); return; }

            try
            {
                lock (_statusLock) _status = new GenerationStatus { Running = true, Step = "Build: receiving…" };

                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await sr.ReadToEndAsync();

                JsonElement apiReq;
                try { apiReq = JsonDocument.Parse(body).RootElement; }
                catch (Exception ex) { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Bad JSON: {ex.Message}" }); return; }

                string? zipBase64 = apiReq.TryGetProperty("projectZipBase64", out var b1) ? b1.GetString() : null;
                string? projectName = apiReq.TryGetProperty("projectName", out var b2) ? b2.GetString() : null;
                string? unityExe = apiReq.TryGetProperty("unityExePath", out var b3) ? b3.GetString() : null;
                string? gcsBucket = apiReq.TryGetProperty("gcsBucket", out var b4) ? b4.GetString() : "aqe-unity-builds";
                string? gcsKeyJson = apiReq.TryGetProperty("gcsKeyJson", out var b5) ? b5.GetString() : null;
                bool development = apiReq.TryGetProperty("development", out var b6) && b6.GetBoolean();

                if (string.IsNullOrWhiteSpace(zipBase64))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectZipBase64 is required." }); return; }
                if (string.IsNullOrWhiteSpace(projectName))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = "projectName is required." }); return; }

                unityExe = string.IsNullOrWhiteSpace(unityExe) ? AppSettings.DefaultUnityExePath : unityExe;
                if (!File.Exists(unityExe))
                { res.StatusCode = 400; await WriteJsonAsync(res, new { error = $"Unity not found: {unityExe}" }); return; }

                var sessionDir = Path.Combine(Path.GetTempPath(), $"UnityBuild_{Guid.NewGuid():N}");
                var projectPath = Path.Combine(sessionDir, projectName);

                try
                {
                    Directory.CreateDirectory(sessionDir);
                    var zipBytes = Convert.FromBase64String(zipBase64);
                    var tempZip = Path.Combine(sessionDir, "source.zip");
                    await File.WriteAllBytesAsync(tempZip, zipBytes);

                    StatusLog($"[Build] Extracting source ZIP ({zipBytes.Length / 1024:N0} KB)…");
                    UiLog?.Invoke($"[Build] Extracting source ZIP ({zipBytes.Length / 1024:N0} KB)…");
                    ZipFile.ExtractToDirectory(tempZip, sessionDir, overwriteFiles: true);

                    if (!Directory.Exists(projectPath))
                    {
                        res.StatusCode = 422;
                        await WriteJsonAsync(res, new { error = $"Expected project folder '{projectName}' not found in zip." });
                        return;
                    }

                    string? keyFilePath = null;
                    if (!string.IsNullOrWhiteSpace(gcsKeyJson))
                    {
                        keyFilePath = Path.Combine(sessionDir, "gcs_key.json");
                        await File.WriteAllTextAsync(keyFilePath,
                            Encoding.UTF8.GetString(Convert.FromBase64String(gcsKeyJson)));
                    }

                    StatusStep("Build: Unity Pass 1 — compile…");
                    var pass1 = await UnityLauncher.Pass1ImportAsync(
                        unityExe, projectPath, StatusLog, CancellationToken.None);

                    if (!pass1.ok)
                    { res.StatusCode = 422; await WriteJsonAsync(res, new { error = $"Pass 1 failed: {pass1.error}", log = _status.Log }); return; }

                    StatusStep("Build: Unity Pass 2 — WebGL build…");
                    var pass2 = await UnityLauncher.Pass2BuildAsync(
                        unityExe, projectPath, StatusLog, CancellationToken.None);

                    if (!pass2.Success)
                    { res.StatusCode = 422; await WriteJsonAsync(res, new { error = $"Pass 2 failed: {pass2.Error}", log = _status.Log }); return; }

                    string? url = null;
                    if (!string.IsNullOrWhiteSpace(gcsBucket) && keyFilePath != null)
                    {
                        StatusStep("Build: uploading to GCS…");
                        url = await UploadBuildToGcsAsync(
                            Path.Combine(projectPath, "Build/WebGL"),
                            gcsBucket, projectName!, keyFilePath);
                    }

                    await WriteJsonAsync(res, new { success = true, url, buildPath = projectPath, log = _status.Log, warnings = Array.Empty<string>() });
                }
                finally
                {
                    try { Directory.Delete(sessionDir, recursive: true); } catch { }
                }
            }
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                _buildToken.Dispose();
            }
        }

        private async Task<string> UploadBuildToGcsAsync(
            string buildFolder, string bucket, string projectName, string keyFilePath)
        {
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
            }
            catch (Exception ex)
            {
                StatusLog($"[GCS] gsutil not available: {ex.Message}. Upload skipped.");
            }

            return $"https://storage.googleapis.com/{bucket}/{projectName}/index.html";
        }


        // ── POST /generate/upload  (multipart + SSE streaming) ─────────────────

        private async Task HandleGenerateUploadAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Parse multipart body ─────────────────────────────────────────
            byte[]? zipBytes;
            string? unityExeArg = null, outputDirArg = null;
            bool force = false;

            try
            {
                var parsed = ParseMultipart(req);
                zipBytes = parsed.FileBytes;
                if (parsed.Fields.TryGetValue("unityExePath", out var ue)) unityExeArg = ue;
                if (parsed.Fields.TryGetValue("outputDir", out var od)) outputDirArg = od;
                if (parsed.Fields.TryGetValue("force", out var fo)) bool.TryParse(fo, out force);
            }
            catch (Exception ex)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = $"Failed to parse multipart body: {ex.Message}" });
                return;
            }

            if (zipBytes == null || zipBytes.Length == 0)
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new
                {
                    error = "No ZIP file found in request.",
                    hint = "Send a multipart/form-data POST with a file field named 'file' containing scene.zip.",
                });
                return;
            }

            // ── Acquire shared processing gate ────────────────────────────
            var token = _gate.TryAcquire();
            if (token == null)
            {
                res.StatusCode = 503;
                await WriteJsonAsync(res, new { error = "Application is currently busy. Cannot process request." });
                return;
            }

            // ── Resolve Unity exe + output dir ──────────────────────────
            var unityExe = !string.IsNullOrWhiteSpace(unityExeArg)
                ? unityExeArg : AppSettings.DefaultUnityExePath;

            if (!File.Exists(unityExe))
            {
                token.Dispose();
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = $"Unity executable not found: {unityExe}", defaultPath = AppSettings.DefaultUnityExePath });
                return;
            }

            var outputDir = !string.IsNullOrWhiteSpace(outputDirArg)
                ? outputDirArg
                : Path.Combine(Path.GetTempPath(), $"UnitySceneGen_out_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);

            // ── Open SSE response ───────────────────────────────────
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.AddHeader("Cache-Control", "no-cache");
            res.AddHeader("X-Accel-Buffering", "no");   // prevent nginx/proxy buffering

            var logQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();

            try
            {
                lock (_statusLock) _status = new GenerationStatus { Running = true, Step = "Upload: starting…" };

                // Capture locals for closure
                var capZip = zipBytes;
                var capExe = unityExe;
                var capOut = outputDir;
                var capForce = force;

                var genTask = Task.Run(() => GenerationEngine.Run(
                    capZip, capExe, capOut, capForce,
                    line =>
                    {
                        logQueue.Enqueue(line);
                        StatusLog(line);
                        UiLog?.Invoke(line);
                        if (line.Contains("Step 1/6")) StatusStep("1/6 — Loading zip");
                        else if (line.Contains("Step 2/6")) StatusStep("2/6 — Resolving templates");
                        else if (line.Contains("Step 3/6")) StatusStep("3/6 — Validating");
                        else if (line.Contains("Step 4/6")) StatusStep("4/6 — Setting up project");
                        else if (line.Contains("Step 5/6")) StatusStep("5/6 — Unity Pass 1 (compile)");
                        else if (line.Contains("Step 6/6")) StatusStep("6/6 — Unity Pass 2 (build)");
                    },
                    CancellationToken.None));

                // Stream log events while generation runs
                using var writer = new StreamWriter(res.OutputStream,
                    new System.Text.UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = false };

                while (!genTask.IsCompleted)
                {
                    await SseFlushLogs(writer, logQueue);
                    await Task.Delay(40);
                }
                await SseFlushLogs(writer, logQueue);  // drain final lines

                var result = await genTask;

                if (!result.Success)
                {
                    lock (_statusLock) { _status.Error = result.Error; _status.Step = "Failed"; }
                    var errPayload = JsonSerializer.Serialize(new { error = result.Error, warnings = result.Warnings }, Json.Compact);
                    await writer.WriteAsync($"event: error\ndata: {EscapeSseData(errPayload)}\n\n");
                    await writer.FlushAsync();
                }
                else
                {
                    // Zip the generated project and embed as base64 in the result SSE event
                    StatusStep("Done — zipping result…");
                    var projectName = Path.GetFileName(result.ProjectPath);
                    var zipOutPath = Path.Combine(outputDir, $"{projectName}_output.zip");
                    ZipFile.CreateFromDirectory(result.ProjectPath, zipOutPath,
                        CompressionLevel.Optimal, includeBaseDirectory: true);

                    var zipOut = await File.ReadAllBytesAsync(zipOutPath);
                    var zipBase64 = Convert.ToBase64String(zipOut);

                    var donePayload = JsonSerializer.Serialize(new
                    {
                        success = true,
                        projectName,
                        sizeKb = zipOut.Length / 1024,
                        warnings = result.Warnings,
                        zipBase64,
                    }, Json.Compact);

                    await writer.WriteAsync($"event: result\ndata: {EscapeSseData(donePayload)}\n\n");
                    await writer.FlushAsync();
                    StatusLog($"[API/upload] Done. {zipOut.Length / 1024:N0} KB delivered via SSE.");
                    UiLog?.Invoke($"[API/upload] Done. {zipOut.Length / 1024:N0} KB delivered via SSE.");
                }
            }
            finally
            {
                lock (_statusLock) { _status.Running = false; }
                token.Dispose();
                try { res.OutputStream.Close(); } catch { }
            }
        }

        private static async Task SseFlushLogs(
            StreamWriter writer,
            System.Collections.Concurrent.ConcurrentQueue<string> queue)
        {
            bool wrote = false;
            while (queue.TryDequeue(out var line))
            {
                await writer.WriteAsync($"data: {EscapeSseData(line)}\n\n");
                wrote = true;
            }
            if (wrote) await writer.FlushAsync();
        }

        // ── Multipart/form-data parser ────────────────────────────────────

        private record MultipartResult(byte[]? FileBytes, Dictionary<string, string> Fields);

        private static MultipartResult ParseMultipart(HttpListenerRequest req)
        {
            var ct = req.ContentType ?? "";
            var bm = Regex.Match(ct, @"boundary=(?:""([^""]+)""|([^\s;]+))", RegexOptions.IgnoreCase);
            if (!bm.Success)
                throw new InvalidDataException("multipart/form-data boundary not found in Content-Type.");

            var boundary = "--" + (bm.Groups[1].Success ? bm.Groups[1].Value : bm.Groups[2].Value);
            var boundaryBytes = Encoding.ASCII.GetBytes(boundary);

            using var ms = new MemoryStream();
            req.InputStream.CopyTo(ms);
            var body = ms.ToArray();

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[]? file = null;

            var positions = FindAllBytes(body, boundaryBytes);

            for (int p = 0; p < positions.Count - 1; p++)
            {
                var start = positions[p] + boundaryBytes.Length;
                if (start + 1 < body.Length && body[start] == '\r' && body[start + 1] == '\n') start += 2;
                else if (start < body.Length && body[start] == '\n') start += 1;

                var end = positions[p + 1];
                if (end >= 2 && body[end - 2] == '\r' && body[end - 1] == '\n') end -= 2;
                else if (end >= 1 && body[end - 1] == '\n') end -= 1;

                var sepIdx = IndexOfBytes(body, new byte[] { 13, 10, 13, 10 }, start, end - start);
                if (sepIdx < 0) continue;

                var headerText = Encoding.UTF8.GetString(body, start, sepIdx - start);
                var bodyStart = sepIdx + 4;
                var bodyLen = end - bodyStart;
                if (bodyLen < 0) continue;

                // Extract the field name from Content-Disposition
                var dispMatch = Regex.Match(headerText,
                    @"Content-Disposition:[^\r\n]*\bname=""([^""]+)""",
                    RegexOptions.IgnoreCase);
                var name = dispMatch.Success ? dispMatch.Groups[1].Value : "";

                bool isFilePart =
                    Regex.IsMatch(headerText, @"\bfilename=", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(headerText, @"Content-Type:\s*application/", RegexOptions.IgnoreCase) ||
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "file", StringComparison.OrdinalIgnoreCase);

                if (isFilePart && file == null)
                {
                    file = new byte[bodyLen];
                    Array.Copy(body, bodyStart, file, 0, bodyLen);
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    fields[name] = Encoding.UTF8.GetString(body, bodyStart, bodyLen);
                }
            }

            return new MultipartResult(file, fields);
        }

        private static List<int> FindAllBytes(byte[] haystack, byte[] needle)
        {
            var result = new List<int>();
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length && ok; j++) ok = haystack[i + j] == needle[j];
                if (ok) result.Add(i);
            }
            return result;
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int offset, int length)
        {
            for (int i = offset; i <= offset + length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length && ok; j++) ok = haystack[i + j] == needle[j];
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>Escape newlines so a log line never breaks an SSE frame.</summary>
        private static string EscapeSseData(string text) =>
            text.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n");

        // ── Root info ─────────────────────────────────────────────────────

        // ── GET /code ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the full contents of Program.cs.
        /// Resolves path by going up from the executable's bin folder to the
        /// project root, then reading Program.cs from that directory.
        /// </summary>
        private async Task HandleGetCodeAsync(HttpListenerResponse res)
        {
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var projectDir = Path.GetDirectoryName(exeDir)!;   // parent of bin/
            var filePath = Path.Combine(projectDir, "Program.cs");

            if (!File.Exists(filePath))
            {
                res.StatusCode = 404;
                await WriteJsonAsync(res, new { error = $"Program.cs not found at: {filePath}" });
                return;
            }

            await WriteRawAsync(res, await File.ReadAllTextAsync(filePath), "text/plain");
        }

        // ── GET /csproj ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the full contents of the project's .csproj file.
        /// Resolves path by going up from the executable's bin folder to the
        /// project root, then finding the first .csproj in that directory.
        /// </summary>
        private async Task HandleGetCsprojAsync(HttpListenerResponse res)
        {
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var projectDir = Path.GetDirectoryName(exeDir)!;   // parent of bin/
            var csprojFiles = Directory.GetFiles(projectDir, "*.csproj");

            if (csprojFiles.Length == 0)
            {
                res.StatusCode = 404;
                await WriteJsonAsync(res, new { error = $"No .csproj file found in: {projectDir}" });
                return;
            }

            await WriteRawAsync(res, await File.ReadAllTextAsync(csprojFiles[0]), "text/plain");
        }

        private object RootInfo() => new
        {
            service = "UnitySceneGen API",
            version = "2.0.0",
            port = _port,
            defaultUnityExePath = AppSettings.DefaultUnityExePath,
            endpoints = new[]
            {
                $"GET  /schema        — component + template catalog",
                $"GET  /status        — live job status",
                $"GET  /swagger       — Swagger UI",
                $"GET  /openapi.json  — OpenAPI 3.0 spec",
                $"GET  /code          — return the full text of Program.cs (main application code)",
                $"GET  /csproj        — return the full text of the project .csproj file",
                $"POST /validate         — validate scene.zip without Unity (< 1 s)",
                $"POST /generate         — generate Unity project from scene.zip (JSON/base64), returns .zip",
                $"POST /generate/upload  — upload scene.zip (multipart/form-data), streams SSE logs + result",
                $"POST /build            — WebGL build from generated project .zip",
            },
        };

        // ── Swagger UI ────────────────────────────────────────────────────

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
    url:             "/openapi.json",
    dom_id:          "#swagger-ui",
    presets:         [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
    layout:          "StandaloneLayout",
    deepLinking:     true,
    tryItOutEnabled: true,
  });
</script>
</body>
</html>
""";

        // ── OpenAPI spec ──────────────────────────────────────────────────

        private string OpenApiSpec()
        {
            var unityPath = AppSettings.DefaultUnityExePath.Replace("\\", "\\\\");
            return $$"""
{
  "openapi": "3.0.3",
  "info": {
    "title": "UnitySceneGen API",
    "version": "2.0.0",
    "description": "Generate a complete Unity project from a scene.zip.\n\n**Input:** Base64-encoded scene.zip containing scene.json + optional scripts/ folder.\n**Output:** Unity project .zip.\n\n**Workflow:**\n1. GET /schema - fetch catalog\n2. Build scene.zip\n3. POST /validate - validate cheaply\n4. POST /generate - run full pipeline (5-20 min)\n5. GET /status - poll progress\n\n**Default Unity path:** {{unityPath}}"
  },
  "servers": [{ "url": "http://localhost:{{_port}}" }],
  "paths": {
    "/schema":   { "get":  { "summary": "Component and template catalog", "operationId": "getSchema",   "responses": { "200": { "description": "Schema" } } } },
    "/status":   { "get":  { "summary": "Live job status",                "operationId": "getStatus",   "responses": { "200": { "description": "Status" } } } },
    "/code":     { "get":  { "summary": "Return the full text of Program.cs", "operationId": "getCode", "responses": { "200": { "description": "Plain text of Program.cs" }, "404": { "description": "File not found" } } } },
    "/csproj":   { "get":  { "summary": "Return the full text of the project .csproj file", "operationId": "getCsproj", "responses": { "200": { "description": "Plain text of the .csproj file" }, "404": { "description": "File not found" } } } },
    "/validate": { "post": { "summary": "Validate scene.zip without Unity", "operationId": "validate",
      "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ZipRequest" } } } },
      "responses": { "200": { "description": "Valid" }, "422": { "description": "Invalid" }, "400": { "description": "Bad request" } }
    } },
    "/generate/upload": { "post": {
      "summary": "Upload scene.zip (multipart) and stream generation logs via SSE",
      "operationId": "generateUpload",
      "description": "Accepts multipart/form-data with a file field containing scene.zip.\n\nResponse is a Server-Sent Events (SSE) stream.\n\n**Event types:**\n- `data:` lines — real-time log output\n- `event: result` — job finished, payload has zipBase64\n- `event: error` — job failed, payload has error details",
      "requestBody": { "required": true, "content": { "multipart/form-data": { "schema": { "$ref": "#/components/schemas/UploadRequest" } } } },
      "responses": {
        "200": { "description": "SSE stream of logs then final result event", "content": { "text/event-stream": { "schema": { "type": "string" } } } },
        "400": { "description": "Bad request" },
        "503": { "description": "Busy — another job is running" }
      }
    } },
    "/generate": { "post": { "summary": "Generate Unity project from scene.zip (JSON body with base64)", "operationId": "generate",
      "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/GenerateRequest" } } } },
      "responses": { "200": { "description": "Unity project zip", "content": { "application/zip": {} } }, "400": { "description": "Bad request" }, "422": { "description": "Failed" }, "503": { "description": "Busy" } }
    } }
  },
  "components": {
    "schemas": {
      "ZipRequest": {
        "type": "object", "required": ["sceneZipBase64"],
        "properties": { "sceneZipBase64": { "type": "string", "description": "Base64-encoded scene.zip" } }
      },
      "UploadRequest": {
        "type": "object", "required": ["file"],
        "properties": {
          "file":         { "type": "string", "format": "binary", "description": "The scene.zip file" },
          "unityExePath": { "type": "string" },
          "outputDir":    { "type": "string" },
          "force":        { "type": "boolean", "default": false }
        }
      },
      "GenerateRequest": {
        "type": "object", "required": ["sceneZipBase64"],
        "properties": {
          "sceneZipBase64": { "type": "string" },
          "unityExePath":   { "type": "string", "default": "{{unityPath}}" },
          "outputDir":      { "type": "string" },
          "force":          { "type": "boolean", "default": false }
        }
      }
    }
  }
}
""";
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static async Task WriteJsonAsync(HttpListenerResponse res, object obj)
            => await WriteRawAsync(res, JsonSerializer.Serialize(obj, Json.Pretty), "application/json");

        private static async Task WriteRawAsync(HttpListenerResponse res, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = $"{contentType}; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.OutputStream.Close();
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 11 — MAIN WINDOW (WPF GUI)
    // ═══════════════════════════════════════════════════════════════════════════

    public class MainWindow : Window
    {
        private readonly TextBox _txtZip;
        private readonly TextBox _txtUnityExe;
        private readonly TextBox _txtOutput;
        private readonly CheckBox _chkForce;
        private readonly Button _btnGenerate;
        private readonly Button _btnOpenFolder;
        private readonly TextBox _txtLog;
        private readonly ScrollViewer _logScroller;
        private readonly Border _statusBar;
        private readonly TextBlock _txtStatus;

        private readonly AppSettings _settings = AppSettings.Load();
        private CancellationTokenSource? _cts;
        private string? _lastOutputPath;

        // API integration
        private readonly ProcessingGate _gate;
        private StackPanel? _controlsPanel;

        private static readonly SolidColorBrush C_Bg = B("#252526");
        private static readonly SolidColorBrush C_Surface = B("#1E1E1E");
        private static readonly SolidColorBrush C_Border = B("#555555");
        private static readonly SolidColorBrush C_FgPrimary = B("#D4D4D4");
        private static readonly SolidColorBrush C_FgMuted = B("#858585");
        private static readonly SolidColorBrush C_Accent = B("#007ACC");
        private static readonly SolidColorBrush C_BtnBg = B("#2D2D30");
        private static readonly SolidColorBrush C_BtnHover = B("#3E3E42");
        private static readonly SolidColorBrush C_Success = B("#28A745");
        private static readonly SolidColorBrush C_Error = B("#DC3545");
        private static readonly SolidColorBrush C_Warning = B("#FFC107");

        public MainWindow(int apiPort, ProcessingGate gate, ApiServer? server = null)
        {
            _gate = gate;
            if (server != null) server.UiLog = line => AppendLog(line);

            Title = "Unity Scene Generator";
            Width = 820;
            Height = 760;
            MinWidth = 640;
            MinHeight = 560;
            Background = C_Bg;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var root = new DockPanel { Margin = new Thickness(18) };

            var header = new TextBlock
            {
                Text = "Unity Scene Generator",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = B("#569CD6"),
                Margin = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var subHeader = new TextBlock
            {
                Text = "Drop a scene.zip → receive a generated Unity project.zip",
                Foreground = C_FgMuted,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 16),
            };
            DockPanel.SetDock(subHeader, Dock.Top);
            root.Children.Add(subHeader);

            // ── API status banner ──────────────────────────────────────────────
            var apiBadge = new TextBlock
            {
                Text = "●  Listening at: " + apiPort,
                Foreground = B("#6FD98E"),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
            };
            var apiBanner = new Border
            {
                Background = B("#0D2A0D"),
                BorderBrush = B("#28A745"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 10),
                Child = apiBadge,
            };
            DockPanel.SetDock(apiBanner, Dock.Top);
            root.Children.Add(apiBanner);

            _txtStatus = new TextBlock
            {
                Text = "Ready — pick a scene.zip to get started",
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(10, 5, 10, 5),
            };
            _statusBar = new Border
            {
                Background = C_Accent,
                CornerRadius = new CornerRadius(2),
                Child = _txtStatus,
                Margin = new Thickness(0, 8, 0, 0),
            };
            DockPanel.SetDock(_statusBar, Dock.Bottom);
            root.Children.Add(_statusBar);

            _controlsPanel = new StackPanel();
            var stack = _controlsPanel;
            DockPanel.SetDock(stack, Dock.Top);
            root.Children.Add(stack);

            stack.Children.Add(Label("scene.zip  (scene.json + optional scripts/ folder)"));
            stack.Children.Add(PathRow(out _txtZip, "Browse…", BrowseZip, "0,0,0,10"));
            _txtZip.Text = _settings.LastZipPath ?? "";

            stack.Children.Add(Label("Unity Editor Executable  (Unity.exe)"));
            stack.Children.Add(PathRow(out _txtUnityExe, "Browse…", BrowseUnity, "0,0,0,10"));
            _txtUnityExe.Text = _settings.LastUnityExePath ?? AppSettings.DefaultUnityExePath;

            stack.Children.Add(Label("Output Folder"));
            stack.Children.Add(PathRow(out _txtOutput, "Browse…", BrowseOutput, "0,0,0,14"));
            _txtOutput.Text = _settings.LastOutputDir ?? "";

            _chkForce = new CheckBox
            {
                Content = "--force  (delete and regenerate from scratch)",
                Foreground = C_FgPrimary,
                Margin = new Thickness(0, 0, 0, 14),
            };
            stack.Children.Add(_chkForce);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };

            _btnGenerate = Btn("▶  Generate", C_Accent, C_Accent, fontSize: 14, bold: true);
            _btnGenerate.Width = 150;
            _btnGenerate.Click += GenerateAsync_Click;
            btnRow.Children.Add(_btnGenerate);

            var btnCancel = Btn("Cancel", C_BtnBg, C_BtnHover);
            btnCancel.Margin = new Thickness(8, 0, 0, 0);
            btnCancel.Click += (_, __) => _cts?.Cancel();
            btnRow.Children.Add(btnCancel);

            _btnOpenFolder = Btn("Open Output Folder", C_BtnBg, C_BtnHover);
            _btnOpenFolder.Margin = new Thickness(8, 0, 0, 0);
            _btnOpenFolder.IsEnabled = false;
            _btnOpenFolder.Click += (_, __) => OpenFolder();
            btnRow.Children.Add(_btnOpenFolder);

            stack.Children.Add(btnRow);

            stack.Children.Add(Label("Log"));

            _txtLog = new TextBox
            {
                Background = C_Surface,
                Foreground = C_FgPrimary,
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
            };

            _logScroller = new ScrollViewer
            {
                Content = _txtLog,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var logBorder = new Border
            {
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Child = _logScroller,
            };
            DockPanel.SetDock(logBorder, Dock.Bottom);
            root.Children.Add(logBorder);

            // Disable the controls panel when the gate is held (by UI or API).
            // The log viewer lives outside _controlsPanel and remains visible.
            _gate.BusyChanged += busy =>
                Dispatcher.InvokeAsync(() => _controlsPanel!.IsEnabled = !busy);

            Content = root;
        }

        private void BrowseZip(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select scene.zip", Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) _txtZip.Text = dlg.FileName;
        }

        private void BrowseUnity(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select Unity.exe", Filter = "Executable (*.exe)|*.exe" };
            if (dlg.ShowDialog() == true) _txtUnityExe.Text = dlg.FileName;
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select output folder" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _txtOutput.Text = dlg.SelectedPath;
        }

        private async void GenerateAsync_Click(object sender, RoutedEventArgs e)
        {
            var zipPath = _txtZip.Text.Trim();
            var unityExe = _txtUnityExe.Text.Trim();
            var outputDir = _txtOutput.Text.Trim();

            if (!File.Exists(zipPath)) { Error("scene.zip not found."); return; }
            if (!File.Exists(unityExe)) { Error("Unity.exe not found."); return; }
            if (string.IsNullOrWhiteSpace(outputDir)) { Error("Output folder is required."); return; }

            // Acquire the shared gate — rejects if the API is already running a job.
            var uiToken = _gate.TryAcquire();
            if (uiToken == null)
            {
                Error("Application is currently busy. Cannot process request.");
                return;
            }

            _settings.LastZipPath = zipPath;
            _settings.LastUnityExePath = unityExe;
            _settings.LastOutputDir = outputDir;
            _settings.Save();

            SetBusy(true);
            _txtLog.Clear();
            _btnOpenFolder.IsEnabled = false;
            _cts = new CancellationTokenSource();

            try
            {
                var zipBytes = await File.ReadAllBytesAsync(zipPath);
                var force = _chkForce.IsChecked == true;

                var result = await Task.Run(() =>
                    GenerationEngine.Run(zipBytes, unityExe, outputDir, force,
                        line => AppendLog(line), _cts.Token));

                if (result.Success)
                {
                    _lastOutputPath = result.ProjectPath;
                    _btnOpenFolder.IsEnabled = true;
                    SetStatus("✓  Generation complete!", C_Success);
                    AppendLog($"\n✓  Done — project at: {result.ProjectPath}");
                }
                else
                {
                    SetStatus($"✗  {result.Error}", C_Error);
                    AppendLog($"\n✗  FAILED: {result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.", C_Warning);
                AppendLog("\n⚠  Generation cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus($"Unexpected error: {ex.Message}", C_Error);
                AppendLog($"\n✗  Exception: {ex}");
            }
            finally
            {
                uiToken.Dispose();  // releases gate → fires BusyChanged(false) → re-enables controls
                SetBusy(false);
            }
        }

        private void OpenFolder()
        {
            if (_lastOutputPath != null && Directory.Exists(_lastOutputPath))
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _lastOutputPath,
                    UseShellExecute = true,
                });
        }

        private void AppendLog(string line) =>
            Dispatcher.InvokeAsync(() => { _txtLog.AppendText(line + "\n"); _logScroller.ScrollToBottom(); });

        private void SetStatus(string text, SolidColorBrush color) =>
            Dispatcher.InvokeAsync(() => { _txtStatus.Text = text; _statusBar.Background = color; });

        private void SetBusy(bool busy) =>
            Dispatcher.InvokeAsync(() =>
            {
                // IsEnabled is managed by ProcessingGate.BusyChanged; update only cosmetics here.
                _btnGenerate.Content = busy ? "⏳ Generating…" : "▶  Generate";
                if (busy) SetStatus("Running…", C_Accent);
            });

        private void Error(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, Foreground = C_FgPrimary, Margin = new Thickness(0, 0, 0, 3) };

        private static Grid PathRow(out TextBox txt, string btnLabel, RoutedEventHandler handler, string margin)
        {
            txt = new TextBox
            {
                Background = B("#1E1E1E"),
                Foreground = B("#D4D4D4"),
                BorderBrush = B("#555555"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 4, 5, 4),
            };
            var btn = Btn(btnLabel, B("#2D2D30"), B("#3E3E42"), width: 80);
            btn.Margin = new Thickness(6, 0, 0, 0);
            btn.Click += handler;

            var g = new Grid { Margin = ParseMargin(margin) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(txt);
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
            return g;
        }

        private static Button Btn(string label, SolidColorBrush bg, SolidColorBrush hover,
            double width = double.NaN, double fontSize = 13, bool bold = false)
        {
            var btn = new Button
            {
                Content = label,
                Background = bg,
                Foreground = Brushes.White,
                BorderBrush = B("#555555"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                FontSize = fontSize,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            };
            if (!double.IsNaN(width)) btn.Width = width;
            btn.MouseEnter += (_, __) => btn.Background = hover;
            btn.MouseLeave += (_, __) => btn.Background = bg;
            btn.IsEnabledChanged += (_, a) => btn.Opacity = (bool)a.NewValue ? 1.0 : 0.4;
            return btn;
        }

        private static SolidColorBrush B(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private static Thickness ParseMargin(string s)
        {
            var p = Array.ConvertAll(s.Split(','), double.Parse);
            return p.Length == 4 ? new Thickness(p[0], p[1], p[2], p[3]) : new Thickness(p[0]);
        }
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // SECTION 12 — ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════════

    public static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        public static int Main(string[] args)
        {
            // Port is fixed at 46001 — no runtime override.
            int apiPort = ApiServer.DefaultPort;

            // ── API-only mode ─────────────────────────────────────────────
            if (Array.Exists(args, a => a.Equals("--api-only", StringComparison.OrdinalIgnoreCase)))
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;

                var gate = new ProcessingGate();
                var server = new ApiServer(apiPort, gate);
                try
                {
                    server.Start();
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }

                Console.WriteLine($"Running in API-only mode on port {apiPort}. Press Ctrl+C to stop.");
                var done = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); done.Set(); };
                done.Wait();
                return 0;
            }

            // ── GUI mode ──────────────────────────────────────────────────
            // The API server always starts automatically when the GUI launches.
            var sharedGate = new ProcessingGate();
            var bgServer = new ApiServer(apiPort, sharedGate);
            try
            {
                bgServer.Start();
            }
            catch (InvalidOperationException ex)
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.Error.WriteLine(ex.Message);
                MessageBox.Show(ex.Message, "Launch Error — Port In Use",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(apiPort, sharedGate, bgServer);
            app.MainWindow = window;
            window.Show();
            int result = app.Run();

            bgServer.Stop();
            return result;
        }
    }

} // namespace UnitySceneGen