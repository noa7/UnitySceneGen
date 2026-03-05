using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class ConfigValidator
    {
        public class ValidationResult
        {
            public bool Valid { get; set; } = true;
            public List<string> Errors { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
            public SceneGenConfig? Config { get; set; }
        }

        public static ValidationResult Validate(string configPath)
        {
            var r = new ValidationResult();

            // 1 — File exists
            if (!File.Exists(configPath))
            {
                r.Errors.Add($"Config file not found: {configPath}");
                r.Valid = false;
                return r;
            }

            // 2 — Parse JSON
            SceneGenConfig? cfg;
            try
            {
                cfg = JsonConvert.DeserializeObject<SceneGenConfig>(File.ReadAllText(configPath));
            }
            catch (JsonException ex)
            {
                r.Errors.Add($"JSON parse error: {ex.Message}");
                r.Valid = false;
                return r;
            }

            if (cfg == null)
            {
                r.Errors.Add("Config deserialized to null. File must be a JSON object.");
                r.Valid = false;
                return r;
            }

            r.Config = cfg;

            // 3 — Project block
            if (cfg.Project == null)
                r.Warnings.Add("No 'project' block — defaults will be used.");
            else if (string.IsNullOrWhiteSpace(cfg.Project.Name))
                r.Errors.Add("project.name is required and cannot be empty.");

            // 4 — At least one scene
            if (cfg.Scenes.Count == 0)
                r.Errors.Add("At least one entry is required in 'scenes'.");

            foreach (var scene in cfg.Scenes)
            {
                if (string.IsNullOrWhiteSpace(scene.Name))
                    r.Errors.Add("A scene entry is missing 'name'.");
                if (string.IsNullOrWhiteSpace(scene.Path))
                    r.Errors.Add($"Scene '{scene.Name}' is missing 'path'.");
                if (scene.Roots.Count == 0)
                    r.Warnings.Add($"Scene '{scene.Name}' has no root GameObjects defined.");
            }

            // 5 — GameObjects: unique IDs, non-empty names
            var goIds = new HashSet<string>();
            foreach (var go in cfg.GameObjects)
            {
                if (string.IsNullOrWhiteSpace(go.Id))
                    r.Errors.Add($"A GameObject entry (name: '{go.Name}') has no 'id'.");
                else if (!goIds.Add(go.Id))
                    r.Errors.Add($"Duplicate GameObject id: '{go.Id}'.");

                if (string.IsNullOrWhiteSpace(go.Name))
                    r.Errors.Add($"GameObject '{go.Id}' has no 'name'.");
            }

            // 6 — Dangling child references
            foreach (var go in cfg.GameObjects)
                foreach (var childId in go.Children)
                    if (!goIds.Contains(childId))
                        r.Errors.Add($"'{go.Id}' references child '{childId}' which does not exist.");

            // 7 — Dangling scene root references
            foreach (var scene in cfg.Scenes)
                foreach (var rootId in scene.Roots)
                    if (!goIds.Contains(rootId))
                        r.Errors.Add($"Scene '{scene.Name}' references root '{rootId}' which does not exist.");

            // 8 — Cycle detection
            if (TryFindCycle(cfg, out var cyclePath))
                r.Errors.Add($"Cycle detected in GameObject hierarchy: {cyclePath}");

            // 9 — Components must have a type
            foreach (var go in cfg.GameObjects)
                foreach (var comp in go.Components)
                    if (string.IsNullOrWhiteSpace(comp.Type))
                        r.Errors.Add($"A component on '{go.Id}' has no 'type'.");

            // 10 — Scripts: unique names, non-empty
            var scriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var script in cfg.Scripts)
            {
                if (string.IsNullOrWhiteSpace(script.Name))
                {
                    r.Errors.Add("A script entry has no 'name'.");
                    continue;
                }

                // Enforce valid C# identifier characters only
                if (!System.Text.RegularExpressions.Regex.IsMatch(script.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    r.Errors.Add($"Script name '{script.Name}' is not a valid C# identifier.");

                if (!scriptNames.Add(script.Name))
                    r.Errors.Add($"Duplicate script name: '{script.Name}'.");
            }

            // 11 — Warn if a component type looks like a user script but isn't declared in scripts[]
            //      (heuristic: no dot in the name means it's not a Unity built-in namespace type)
            if (scriptNames.Count > 0 || cfg.Scripts.Count > 0)
            {
                foreach (var go in cfg.GameObjects)
                    foreach (var comp in go.Components)
                    {
                        var t = comp.Type ?? "";
                        if (!t.Contains('.') && !scriptNames.Contains(t))
                            r.Warnings.Add(
                                $"Component type '{t}' on '{go.Id}' has no namespace and is not " +
                                $"declared in 'scripts'. If it is a custom MonoBehaviour, add it to " +
                                $"the 'scripts' array so it is compiled before the scene is built.");
                    }
            }

            r.Valid = r.Errors.Count == 0;
            return r;
        }

        private static bool TryFindCycle(SceneGenConfig cfg, out string cyclePath)
        {
            cyclePath = "";
            var byId = cfg.GameObjects.ToDictionary(g => g.Id);
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();
            var stack = new List<string>();
            string foundCycle = "";   // local — avoids the out-param-in-lambda restriction

            bool Dfs(string id)
            {
                if (inStack.Contains(id))
                {
                    stack.Add(id);
                    foundCycle = string.Join(" → ", stack);
                    return true;
                }
                if (visited.Contains(id)) return false;

                visited.Add(id);
                inStack.Add(id);
                stack.Add(id);

                if (byId.TryGetValue(id, out var go))
                    foreach (var child in go.Children)
                        if (Dfs(child)) return true;

                stack.RemoveAt(stack.Count - 1);
                inStack.Remove(id);
                return false;
            }

            foreach (var go in cfg.GameObjects)
            {
                if (Dfs(go.Id))
                {
                    cyclePath = foundCycle;
                    return true;
                }
            }

            return false;
        }
    }
}