using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    // ─────────────────────────────────────────────────────────────────
    // Template definition
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Describes a reusable component bundle.
    ///
    /// When used in a .json template file the structure is:
    /// {
    ///   "name": "ui/button",
    ///   "description": "...",
    ///   "components": [ ... ],
    ///   "propMappings": {
    ///     "label":      "TMPro.TextMeshProUGUI.text",
    ///     "labelColor": "TMPro.TextMeshProUGUI.color",
    ///     "bgColor":    "UnityEngine.UI.Image.color"
    ///   }
    /// }
    /// </summary>
    public class TemplateDefinition
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("components")] public List<ComponentConfig> Components { get; set; } = new();

        /// <summary>
        /// Maps friendly template prop names to "ComponentType.propName".
        /// When a GameObject sets templateProps: { "label": "OK" } and the mapping
        /// is "label" → "TMPro.TextMeshProUGUI.text", the resolver finds the
        /// TextMeshProUGUI component in the expanded list and sets its "text" prop.
        /// </summary>
        [JsonProperty("propMappings")] public Dictionary<string, string> PropMappings { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────
    // Resolver
    // ─────────────────────────────────────────────────────────────────

    public static class TemplateResolver
    {
        // ── Built-in template library ─────────────────────────────────
        //
        // These are always available — no files needed.
        // They are also returned by GET /schema so an LLM can discover them.

        private static readonly Dictionary<string, TemplateDefinition> Builtins =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── Camera ───────────────────────────────────────────────
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

                // ── Lighting ─────────────────────────────────────────────
                ["light/directional"] = new TemplateDefinition
                {
                    Name = "light/directional",
                    Description = "Directional light. Pair with Transform localEulerAngles [50,-30,0] for a natural sun angle.",
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

                // ── UI: Canvas root ───────────────────────────────────────
                ["ui/canvas"] = new TemplateDefinition
                {
                    Name = "ui/canvas",
                    Description = "Full-screen overlay Canvas with CanvasScaler (1920x1080) and GraphicRaycaster. Use as the root of all UI.",
                    Components = new List<ComponentConfig>
                {
                    new() { Type = "UnityEngine.Canvas", Props = new()
                    {
                        ["renderMode"] = "ScreenSpaceOverlay",
                    }},
                    new() { Type = "UnityEngine.UI.CanvasScaler", Props = new()
                    {
                        ["uiScaleMode"]        = "ScaleWithScreenSize",
                        ["referenceResolution"] = new[] { 1920, 1080 },
                        ["matchWidthOrHeight"]  = 0.5,
                    }},
                    new() { Type = "UnityEngine.UI.GraphicRaycaster" },
                },
                    PropMappings = new()
                    {
                        ["referenceResolution"] = "UnityEngine.UI.CanvasScaler.referenceResolution",
                        ["matchWidthOrHeight"] = "UnityEngine.UI.CanvasScaler.matchWidthOrHeight",
                    },
                },

                // ── UI: EventSystem ───────────────────────────────────────
                ["ui/eventsystem"] = new TemplateDefinition
                {
                    Name = "ui/eventsystem",
                    Description = "EventSystem + StandaloneInputModule. Required for click/tap events. Add exactly one per scene.",
                    Components = new List<ComponentConfig>
                {
                    new() { Type = "UnityEngine.EventSystems.EventSystem" },
                    new() { Type = "UnityEngine.EventSystems.StandaloneInputModule" },
                },
                    PropMappings = new(),
                },

                // ── UI: Panel ─────────────────────────────────────────────
                ["ui/panel"] = new TemplateDefinition
                {
                    Name = "ui/panel",
                    Description = "RectTransform filling its parent + Image. Use as a container or background.",
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

                // ── UI: Button ────────────────────────────────────────────
                ["ui/button"] = new TemplateDefinition
                {
                    Name = "ui/button",
                    Description = "Clickable button: RectTransform + Image + Button + TextMeshProUGUI label.",
                    Components = new List<ComponentConfig>
                {
                    new() { Type = "UnityEngine.RectTransform", Props = new()
                    {
                        ["sizeDelta"] = new[] { 200, 60 },
                    }},
                    new() { Type = "UnityEngine.UI.Image", Props = new()
                    {
                        ["color"] = "#1A73E8FF",
                    }},
                    new() { Type = "UnityEngine.UI.Button" },
                    new() { Type = "TMPro.TextMeshProUGUI", Props = new()
                    {
                        ["text"]               = "Button",
                        ["fontSize"]           = 24,
                        ["alignment"]          = "Center",
                        ["color"]              = "#FFFFFFFF",
                        ["enableWordWrapping"] = false,
                    }},
                },
                    PropMappings = new()
                    {
                        ["label"] = "TMPro.TextMeshProUGUI.text",
                        ["labelColor"] = "TMPro.TextMeshProUGUI.color",
                        ["fontSize"] = "TMPro.TextMeshProUGUI.fontSize",
                        ["bgColor"] = "UnityEngine.UI.Image.color",
                        ["sizeDelta"] = "UnityEngine.RectTransform.sizeDelta",
                        ["interactable"] = "UnityEngine.UI.Button.interactable",
                    },
                },

                // ── UI: Label ─────────────────────────────────────────────
                ["ui/label"] = new TemplateDefinition
                {
                    Name = "ui/label",
                    Description = "RectTransform + TextMeshProUGUI. Use for headings, body text, HUD values.",
                    Components = new List<ComponentConfig>
                {
                    new() { Type = "UnityEngine.RectTransform", Props = new()
                    {
                        ["anchorMin"] = new[] { 0, 0 },
                        ["anchorMax"] = new[] { 1, 1 },
                        ["offsetMin"] = new[] { 0, 0 },
                        ["offsetMax"] = new[] { 0, 0 },
                    }},
                    new() { Type = "TMPro.TextMeshProUGUI", Props = new()
                    {
                        ["text"]      = "Label",
                        ["fontSize"]  = 36,
                        ["alignment"] = "Center",
                        ["color"]     = "#FFFFFFFF",
                    }},
                },
                    PropMappings = new()
                    {
                        ["text"] = "TMPro.TextMeshProUGUI.text",
                        ["color"] = "TMPro.TextMeshProUGUI.color",
                        ["fontSize"] = "TMPro.TextMeshProUGUI.fontSize",
                        ["alignment"] = "TMPro.TextMeshProUGUI.alignment",
                        ["anchorMin"] = "UnityEngine.RectTransform.anchorMin",
                        ["anchorMax"] = "UnityEngine.RectTransform.anchorMax",
                        ["offsetMin"] = "UnityEngine.RectTransform.offsetMin",
                        ["offsetMax"] = "UnityEngine.RectTransform.offsetMax",
                        ["sizeDelta"] = "UnityEngine.RectTransform.sizeDelta",
                        ["anchoredPosition"] = "UnityEngine.RectTransform.anchoredPosition",
                    },
                },

                // ── Physics ───────────────────────────────────────────────
                ["physics/rigidbody"] = new TemplateDefinition
                {
                    Name = "physics/rigidbody",
                    Description = "Rigidbody + BoxCollider. Standard dynamic physics object.",
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
                        ["colliderSize"] = "UnityEngine.BoxCollider.size",
                    },
                },

                ["physics/trigger"] = new TemplateDefinition
                {
                    Name = "physics/trigger",
                    Description = "BoxCollider with isTrigger = true. Use for pickup zones, detection areas.",
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

                // ── Audio ─────────────────────────────────────────────────
                ["audio/source"] = new TemplateDefinition
                {
                    Name = "audio/source",
                    Description = "AudioSource. Assign a clip at runtime via script.",
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

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// Expands all template references in the config in-place.
        /// Call this after ConfigLoader.Load and before ConfigValidator.ValidateConfig.
        ///
        /// Expansion rules:
        ///   1. Template components become the base component list.
        ///   2. templateProps are applied via the template's propMappings.
        ///   3. Any components explicitly listed on the GameObject are merged on top —
        ///      explicit always wins over template defaults (by component type).
        ///   4. Template and TemplateProps fields are cleared after expansion.
        /// </summary>
        /// <param name="cfg">The config to expand in-place.</param>
        /// <param name="templatesDir">Optional directory to search for .json template files.</param>
        /// <param name="warnings">Any warnings produced during resolution are appended here.</param>
        public static void Resolve(
            SceneGenConfig cfg,
            string? templatesDir = null,
            List<string>? warnings = null)
        {
            // Build a file-based template cache for this resolution pass
            var fileCache = templatesDir != null
                ? LoadFileTemplates(templatesDir, warnings)
                : new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var go in cfg.GameObjects)
            {
                if (string.IsNullOrWhiteSpace(go.Template))
                    continue;

                var tpl = FindTemplate(go.Template, fileCache);

                if (tpl == null)
                {
                    warnings?.Add(
                        $"Template '{go.Template}' referenced by '{go.Id}' was not found. " +
                        $"Built-in templates: {string.Join(", ", Builtins.Keys)}.");
                    go.Template = null;
                    go.TemplateProps = null;
                    continue;
                }

                // 1. Deep-copy template components as the base
                var resolved = tpl.Components
                    .Select(c => new ComponentConfig
                    {
                        Type = c.Type,
                        Props = c.Props == null
                            ? null
                            : new Dictionary<string, object>(c.Props),
                    })
                    .ToList();

                // 2. Apply templateProps via propMappings
                if (go.TemplateProps != null)
                {
                    foreach (var (friendlyKey, friendlyValue) in go.TemplateProps)
                    {
                        if (!tpl.PropMappings.TryGetValue(friendlyKey, out var mapping))
                        {
                            warnings?.Add(
                                $"GameObject '{go.Id}' template '{go.Template}': " +
                                $"templateProp '{friendlyKey}' is not a mapped prop for this template. " +
                                $"Available: {string.Join(", ", tpl.PropMappings.Keys)}.");
                            continue;
                        }

                        // mapping = "ComponentType.propName"
                        var lastDot = mapping.LastIndexOf('.');
                        var compType = mapping.Substring(0, lastDot);
                        var propName = mapping.Substring(lastDot + 1);

                        var comp = resolved.FirstOrDefault(c =>
                            string.Equals(c.Type, compType, StringComparison.OrdinalIgnoreCase));

                        if (comp == null)
                        {
                            warnings?.Add(
                                $"GameObject '{go.Id}' template '{go.Template}': " +
                                $"propMapping '{friendlyKey}' targets component type '{compType}' " +
                                $"which is not in the template's component list.");
                            continue;
                        }

                        comp.Props ??= new Dictionary<string, object>();
                        comp.Props[propName] = friendlyValue;
                    }
                }

                // 3. Merge override components on top (explicit always wins by type)
                foreach (var overrideComp in go.Components)
                {
                    var existing = resolved.FirstOrDefault(c =>
                        string.Equals(c.Type, overrideComp.Type, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // Merge props — override wins on a per-key basis
                        if (overrideComp.Props != null)
                        {
                            existing.Props ??= new Dictionary<string, object>();
                            foreach (var (k, v) in overrideComp.Props)
                                existing.Props[k] = v;
                        }
                    }
                    else
                    {
                        // Component type not in template — add it
                        resolved.Add(overrideComp);
                    }
                }

                // 4. Replace and clear
                go.Components = resolved;
                go.Template = null;
                go.TemplateProps = null;
            }
        }

        /// <summary>
        /// Returns the built-in template catalog for inclusion in GET /schema.
        /// </summary>
        public static IReadOnlyDictionary<string, TemplateDefinition> GetBuiltins()
            => Builtins;

        // ── Template lookup ───────────────────────────────────────────

        private static TemplateDefinition? FindTemplate(
            string name,
            Dictionary<string, TemplateDefinition> fileCache)
        {
            // File templates take precedence so callers can override builtins
            if (fileCache.TryGetValue(name, out var fileTpl)) return fileTpl;
            if (Builtins.TryGetValue(name, out var builtin)) return builtin;
            return null;
        }

        private static Dictionary<string, TemplateDefinition> LoadFileTemplates(
            string dir,
            List<string>? warnings)
        {
            var cache = new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(dir)) return cache;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json",
                         SearchOption.AllDirectories))
            {
                try
                {
                    var tpl = JsonConvert.DeserializeObject<TemplateDefinition>(
                                  File.ReadAllText(file));
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
}