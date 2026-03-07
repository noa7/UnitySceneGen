using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class ConfigValidator
    {
        public class ValidationResult
        {
            public bool         Valid    { get; set; } = true;
            public List<string> Errors   { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
            public SceneGenConfig? Config { get; set; }
        }

        // ── Built-in Unity tags / layers ──────────────────────────────

        private static readonly HashSet<string> BuiltinTags = new(StringComparer.Ordinal)
        {
            "Untagged","Respawn","Finish","EditorOnly",
            "MainCamera","Player","GameController",
        };

        private static readonly HashSet<string> BuiltinLayers = new(StringComparer.Ordinal)
        {
            "Default","TransparentFX","Ignore Raycast","Water","UI",
        };

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// Loads a config from disk (manifest or single-file format), resolves
        /// templates, then validates. Returns the merged + expanded SceneGenConfig
        /// in result.Config on success.
        ///
        /// This is the entry point used by the API server's /validate handler,
        /// which writes a temp file and calls Validate(path).
        /// </summary>
        public static ValidationResult Validate(string configPath)
        {
            var r = new ValidationResult();

            // 1 — Load (handles manifest and single-file transparently)
            ConfigLoader.LoadResult loaded;
            try
            {
                loaded = ConfigLoader.Load(configPath);
            }
            catch (Exception ex)
            {
                r.Errors.Add(ex.Message);
                r.Valid = false;
                return r;
            }

            // 2 — Resolve templates (built-ins always available; file templates if dir exists)
            var templateWarnings = new List<string>();
            TemplateResolver.Resolve(loaded.Config, loaded.TemplatesDir, templateWarnings);
            r.Warnings.AddRange(templateWarnings);

            // 3 — Semantic validation
            var inner = ValidateConfig(loaded.Config);
            r.Errors.AddRange(inner.Errors);
            r.Warnings.AddRange(inner.Warnings);
            r.Valid  = inner.Valid;
            r.Config = loaded.Config;
            return r;
        }

        /// <summary>
        /// Validates an already-loaded and template-resolved SceneGenConfig.
        /// Used by GenerationEngine after it has already run ConfigLoader + TemplateResolver.
        /// </summary>
        public static ValidationResult ValidateConfig(SceneGenConfig cfg)
        {
            var r = new ValidationResult { Config = cfg };

            // ── 1. Project ────────────────────────────────────────────
            if (cfg.Project == null)
                r.Warnings.Add("No 'project' block — defaults will be used (name: MyProject, unityVersion: 2022.3.20f1).");
            else if (string.IsNullOrWhiteSpace(cfg.Project.Name))
                r.Errors.Add("'project.name' is required and cannot be empty.");

            // ── 2. Scenes ─────────────────────────────────────────────
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

            // ── 3. GameObject IDs ─────────────────────────────────────
            var goIds = new HashSet<string>();
            foreach (var go in cfg.GameObjects)
            {
                if (string.IsNullOrWhiteSpace(go.Id))
                    r.Errors.Add($"A GameObject (name: '{go.Name}') has no 'id'. " +
                                 $"Every GameObject needs a unique dot-separated id, e.g. \"go.player\".");
                else if (!goIds.Add(go.Id))
                    r.Errors.Add($"Duplicate GameObject id: '{go.Id}'. All ids must be unique.");

                if (string.IsNullOrWhiteSpace(go.Name))
                    r.Errors.Add($"GameObject '{go.Id}' has no 'name'.");
            }

            // ── 4. Dangling references ────────────────────────────────
            foreach (var go in cfg.GameObjects)
                foreach (var childId in go.Children)
                    if (!goIds.Contains(childId))
                        r.Errors.Add($"GameObject '{go.Id}' lists child '{childId}' in 'children', " +
                                     $"but no GameObject with that id exists.");

            foreach (var scene in cfg.Scenes)
                foreach (var rootId in scene.Roots)
                    if (!goIds.Contains(rootId))
                        r.Errors.Add($"Scene '{scene.Name}' lists root '{rootId}', " +
                                     $"but no GameObject with that id exists.");

            // ── 5. Cycle detection ────────────────────────────────────
            if (TryFindCycle(cfg, out var cyclePath))
                r.Errors.Add($"Cycle detected in GameObject hierarchy: {cyclePath}. " +
                             $"A GameObject cannot be its own ancestor.");

            // ── 6. ID hierarchy convention ────────────────────────────
            //   Child ids should start with the parent id + "."
            //   This is a convention warning only — generation still proceeds.
            var byId = cfg.GameObjects.ToDictionary(g => g.Id);
            foreach (var go in cfg.GameObjects)
            {
                foreach (var childId in go.Children)
                {
                    if (!goIds.Contains(childId)) continue; // already flagged above
                    if (!childId.StartsWith(go.Id + ".", StringComparison.Ordinal))
                    {
                        var suggested = go.Id + "." +
                            (byId.TryGetValue(childId, out var child)
                                ? child.Name.ToLowerInvariant().Replace(' ', '_')
                                : childId);
                        r.Warnings.Add(
                            $"ID convention: '{childId}' is a child of '{go.Id}' but its id does not " +
                            $"start with '{go.Id}.'. Suggested id: '{suggested}'. " +
                            $"Following the convention makes the hierarchy self-documenting.");
                    }
                }
            }

            // ── 7. Unresolved templates ───────────────────────────────
            foreach (var go in cfg.GameObjects)
                if (!string.IsNullOrWhiteSpace(go.Template))
                    r.Errors.Add(
                        $"GameObject '{go.Id}' still has template '{go.Template}' after resolution. " +
                        $"This means the template was not found. " +
                        $"Built-in templates: {string.Join(", ", TemplateResolver.GetBuiltins().Keys)}.");

            // ── 8. Component types ────────────────────────────────────
            var scriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in cfg.Scripts)
                if (!string.IsNullOrWhiteSpace(s.Name))
                    scriptNames.Add(s.Name);

            foreach (var go in cfg.GameObjects)
                foreach (var comp in go.Components)
                {
                    if (string.IsNullOrWhiteSpace(comp.Type))
                        r.Errors.Add($"A component on '{go.Id}' has no 'type'. " +
                                     $"Specify a fully-qualified Unity type, e.g. \"UnityEngine.Camera\".");

                    if (comp.Props != null)
                        ValidateProps(comp.Props, go.Id, comp.Type, goIds, r);
                }

            // ── 9. Tag / layer usage ──────────────────────────────────
            var declaredTags   = cfg.Settings?.Tags   != null
                ? new HashSet<string>(cfg.Settings.Tags,   StringComparer.Ordinal)
                : new HashSet<string>();
            var declaredLayers = cfg.Settings?.Layers != null
                ? new HashSet<string>(cfg.Settings.Layers, StringComparer.Ordinal)
                : new HashSet<string>();

            foreach (var go in cfg.GameObjects)
            {
                if (!string.IsNullOrEmpty(go.Tag)
                    && go.Tag != "Untagged"
                    && !BuiltinTags.Contains(go.Tag)
                    && !declaredTags.Contains(go.Tag))
                    r.Errors.Add(
                        $"GameObject '{go.Id}' uses tag '{go.Tag}' which is not declared in " +
                        $"settings.tags. Add \"{go.Tag}\" to settings.tags, or use a built-in: " +
                        string.Join(", ", BuiltinTags.Select(t => $"\"{t}\"")));

                if (!string.IsNullOrEmpty(go.Layer)
                    && go.Layer != "Default"
                    && !BuiltinLayers.Contains(go.Layer)
                    && !declaredLayers.Contains(go.Layer))
                    r.Errors.Add(
                        $"GameObject '{go.Id}' uses layer '{go.Layer}' which is not declared in " +
                        $"settings.layers. Add \"{go.Layer}\" to settings.layers, or use a built-in: " +
                        string.Join(", ", BuiltinLayers.Select(l => $"\"{l}\"")));
            }

            // ── 10. Scripts ───────────────────────────────────────────
            var seenScriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var script in cfg.Scripts)
            {
                if (string.IsNullOrWhiteSpace(script.Name))
                {
                    r.Errors.Add("A script entry has no 'name'.");
                    continue;
                }

                if (!Regex.IsMatch(script.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    r.Errors.Add(
                        $"Script name '{script.Name}' is not a valid C# identifier. " +
                        $"Use letters, digits, and underscores only; must not start with a digit.");

                if (!seenScriptNames.Add(script.Name))
                    r.Errors.Add($"Duplicate script name: '{script.Name}'.");

                // Validate File path exists (if set)
                if (!string.IsNullOrWhiteSpace(script.File) && !File.Exists(script.File))
                    r.Errors.Add(
                        $"Script '{script.Name}' has file: \"{script.File}\" but the file does not exist.");
            }

            // ── 11. Warn on likely-custom component types not in scripts[] ──
            if (scriptNames.Count > 0)
                foreach (var go in cfg.GameObjects)
                    foreach (var comp in go.Components)
                    {
                        var t = comp.Type ?? "";
                        if (!t.Contains('.') && !scriptNames.Contains(t))
                            r.Warnings.Add(
                                $"Component type '{t}' on '{go.Id}' has no namespace and is not " +
                                $"declared in 'scripts'. If it is a custom MonoBehaviour, add it to " +
                                $"'scripts' so it compiles before the scene is built.");
                    }

            r.Valid = r.Errors.Count == 0;
            return r;
        }

        // ── Prop-level validation ─────────────────────────────────────

        private static void ValidateProps(
            Dictionary<string, object> props,
            string goId, string compType,
            HashSet<string> goIds,
            ValidationResult r)
        {
            foreach (var (key, rawValue) in props)
            {
                if (rawValue == null) continue;

                var strVal   = AsString(rawValue);
                var arrayVal = rawValue as JArray;

                if (strVal != null)
                {
                    // Color: must be #RRGGBBAA (exactly 8 hex digits)
                    if (strVal.StartsWith('#'))
                    {
                        if (!IsValidColor(strVal))
                            r.Errors.Add(
                                $"'{compType}' on '{goId}': prop '{key}' = \"{strVal}\" " +
                                $"is not a valid color. Colors must be #RRGGBBAA (exactly 8 hex digits " +
                                $"including alpha), e.g. \"#FF000080\". Got {strVal.Length - 1} hex char(s).");
                    }
                    // ref: target must exist
                    else if (strVal.StartsWith("ref:", StringComparison.Ordinal))
                    {
                        var refId = strVal.Substring(4);
                        if (string.IsNullOrWhiteSpace(refId))
                            r.Errors.Add(
                                $"'{compType}' on '{goId}': prop '{key}' is \"ref:\" with no id. " +
                                $"Use \"ref:<gameObjectId>\", e.g. \"ref:go.camera\".");
                        else if (!goIds.Contains(refId))
                            r.Errors.Add(
                                $"'{compType}' on '{goId}': prop '{key}' = \"{strVal}\" but no " +
                                $"GameObject with id '{refId}' exists.");
                    }
                }
                else if (arrayVal != null)
                {
                    int len = arrayVal.Count;
                    if (len != 2 && len != 3 && len != 4)
                        r.Errors.Add(
                            $"'{compType}' on '{goId}': prop '{key}' is an array with {len} element(s). " +
                            $"Must have exactly 2 (Vector2), 3 (Vector3), or 4 (Vector4) elements.");

                    foreach (var el in arrayVal)
                        if (el.Type != JTokenType.Integer && el.Type != JTokenType.Float)
                            r.Errors.Add(
                                $"'{compType}' on '{goId}': prop '{key}' array element \"{el}\" is not " +
                                $"a number. All vector array elements must be numeric.");
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static string? AsString(object value)
        {
            if (value is string s) return s;
            if (value is JValue jv && jv.Type == JTokenType.String) return jv.Value<string>();
            return null;
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
            var byId    = cfg.GameObjects.ToDictionary(g => g.Id);
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();
            var stack   = new List<string>();
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
}
