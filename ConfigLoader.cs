using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    /// <summary>
    /// Loads a scene config from disk in either of two formats:
    ///
    ///   MANIFEST FORMAT — a small entry-point JSON that references separate files:
    ///     scene.manifest.json  →  scenes/*.scene.json, objects/*.go.json, scripts/*.cs
    ///
    ///   SINGLE-FILE FORMAT — the classic all-in-one JSON (still fully supported).
    ///
    /// Both formats produce the same in-memory SceneGenConfig and are treated
    /// identically by everything downstream (TemplateResolver, Validator, Builder).
    ///
    /// ── Manifest format ─────────────────────────────────────────────────────────
    ///
    ///   scene.manifest.json:
    ///   {
    ///     "project":  { "name": "MyProject", "unityVersion": "2022.3.20f1" },
    ///     "settings": { "tags": ["Player"], "layers": ["Gameplay"] },
    ///     "scenes":   ["scenes/Main.scene.json"],
    ///     "objects":  ["objects/camera.go.json", "objects/player.go.json"],
    ///     "scripts":  ["scripts/PlayerController.cs", "scripts/EnemyAI.cs"],
    ///     "templatesDir": "templates/"
    ///   }
    ///
    ///   scenes/Main.scene.json:
    ///   { "name": "Main", "path": "Assets/Scenes/Main.unity", "roots": ["go.root"] }
    ///
    ///   objects/camera.go.json:
    ///   { "id": "go.root.camera", "name": "Main Camera", "template": "camera/main" }
    ///
    ///   scripts/PlayerController.cs:
    ///   (a real .cs file — copied directly into Assets/Scripts/)
    ///
    /// ── Detection ────────────────────────────────────────────────────────────────
    ///   A file is a manifest when its root JSON contains an "objects" key, or
    ///   when its "scenes" or "scripts" arrays contain strings instead of objects.
    /// </summary>
    public static class ConfigLoader
    {
        // ── Public API ────────────────────────────────────────────────

        public record LoadResult(
            SceneGenConfig Config,
            string         ConfigDir,
            string?        TemplatesDir);

        /// <summary>
        /// Loads a manifest or single-config JSON file and returns the merged
        /// SceneGenConfig plus the directory it was loaded from (used to resolve
        /// relative paths for script File references and template directories).
        /// </summary>
        public static LoadResult Load(string configPath, Action<string>? log = null)
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Config file not found: {configPath}");

            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            JObject root;

            try
            {
                root = JObject.Parse(File.ReadAllText(configPath));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"JSON parse error in '{configPath}': {ex.Message}");
            }

            if (IsManifest(root))
            {
                log?.Invoke($"[ConfigLoader] Manifest format detected: {configPath}");
                return LoadManifest(root, configDir, log);
            }

            log?.Invoke($"[ConfigLoader] Single-file format detected: {configPath}");
            return LoadSingleFile(root, configDir);
        }

        // ── Manifest loading ──────────────────────────────────────────

        private static bool IsManifest(JObject root)
        {
            // Manifest has "objects" key (not present in single config)
            if (root.ContainsKey("objects")) return true;

            // Or its "scenes" array contains strings, not objects
            if (root["scenes"] is JArray scenes && scenes.Count > 0
                && scenes[0].Type == JTokenType.String) return true;

            // Or its "scripts" array contains strings (.cs paths), not objects
            if (root["scripts"] is JArray scripts && scripts.Count > 0
                && scripts[0].Type == JTokenType.String) return true;

            return false;
        }

        private static LoadResult LoadManifest(
            JObject root, string manifestDir, Action<string>? log)
        {
            var cfg = new SceneGenConfig();

            // ── Inline project + settings (always in the manifest itself) ──
            cfg.Project  = root["project"]?.ToObject<ProjectConfig>();
            cfg.Settings = root["settings"]?.ToObject<SettingsConfig>();

            // ── Scenes — each is a path to a .scene.json file ────────────
            if (root["scenes"] is JArray sceneRefs)
            {
                foreach (var token in sceneRefs)
                {
                    var relPath = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(relPath)) continue;

                    var absPath = Resolve(manifestDir, relPath);
                    log?.Invoke($"[ConfigLoader]   scene ← {relPath}");

                    SceneConfig? scene;
                    try   { scene = JsonConvert.DeserializeObject<SceneConfig>(File.ReadAllText(absPath)); }
                    catch (Exception ex) { throw new InvalidDataException($"Could not load scene file '{absPath}': {ex.Message}"); }

                    if (scene != null) cfg.Scenes.Add(scene);
                }
            }

            // ── Objects — each is a path to a .go.json file ─────────────
            if (root["objects"] is JArray objectRefs)
            {
                foreach (var token in objectRefs)
                {
                    var relPath = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(relPath)) continue;

                    var absPath = Resolve(manifestDir, relPath);
                    log?.Invoke($"[ConfigLoader]   object ← {relPath}");

                    GameObjectConfig? go;
                    try   { go = JsonConvert.DeserializeObject<GameObjectConfig>(File.ReadAllText(absPath)); }
                    catch (Exception ex) { throw new InvalidDataException($"Could not load object file '{absPath}': {ex.Message}"); }

                    if (go != null) cfg.GameObjects.Add(go);
                }
            }

            // ── Scripts — each is a path to a .cs file ──────────────────
            if (root["scripts"] is JArray scriptRefs)
            {
                foreach (var token in scriptRefs)
                {
                    var relPath = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(relPath)) continue;

                    var absPath = Resolve(manifestDir, relPath);
                    if (!File.Exists(absPath))
                        throw new FileNotFoundException(
                            $"Script file referenced in manifest not found: '{absPath}' (from '{relPath}')");

                    var scriptName = Path.GetFileNameWithoutExtension(absPath);
                    log?.Invoke($"[ConfigLoader]   script ← {relPath}  (name: {scriptName})");

                    cfg.Scripts.Add(new ScriptConfig
                    {
                        Name = scriptName,
                        File = absPath,   // absolute — ProjectCreator copies it directly
                    });
                }
            }

            // ── Templates directory (optional) ───────────────────────────
            string? templatesDir = null;
            var templatesDirToken = root["templatesDir"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(templatesDirToken))
            {
                templatesDir = Resolve(manifestDir, templatesDirToken);
                log?.Invoke($"[ConfigLoader]   templatesDir: {templatesDir}");
            }
            else
            {
                // Auto-detect: if a "templates/" folder exists next to the manifest, use it
                var auto = Path.Combine(manifestDir, "templates");
                if (Directory.Exists(auto))
                {
                    templatesDir = auto;
                    log?.Invoke($"[ConfigLoader]   templatesDir (auto): {templatesDir}");
                }
            }

            log?.Invoke($"[ConfigLoader] Merged: {cfg.Scenes.Count} scene(s), " +
                        $"{cfg.GameObjects.Count} object(s), {cfg.Scripts.Count} script(s).");

            return new LoadResult(cfg, manifestDir, templatesDir);
        }

        // ── Single-file loading ───────────────────────────────────────

        private static LoadResult LoadSingleFile(JObject root, string configDir)
        {
            var cfg = JsonConvert.DeserializeObject<SceneGenConfig>(root.ToString())
                      ?? new SceneGenConfig();

            // Resolve any script File paths that are relative
            foreach (var script in cfg.Scripts)
            {
                if (!string.IsNullOrWhiteSpace(script.File)
                    && !Path.IsPathRooted(script.File))
                {
                    script.File = Path.GetFullPath(
                        Path.Combine(configDir, script.File));
                }
            }

            // Auto-detect templates/ folder next to config
            string? templatesDir = null;
            var auto = Path.Combine(configDir, "templates");
            if (Directory.Exists(auto))
                templatesDir = auto;

            return new LoadResult(cfg, configDir, templatesDir);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static string Resolve(string baseDir, string relPath)
            => Path.GetFullPath(Path.Combine(baseDir, relPath));
    }
}
