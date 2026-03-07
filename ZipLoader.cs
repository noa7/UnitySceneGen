using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    /// <summary>
    /// Loads a scene config from a zip archive with this structure:
    ///
    ///   MyScene.zip
    ///   ├── scene.json          ← required — project, settings, scenes, gameObjects, templates
    ///   └── scripts/            ← optional — real .cs files, copied as-is into Assets/Scripts/
    ///       ├── PlayerController.cs
    ///       └── Rotator.cs
    ///
    /// Rules:
    ///   - scene.json must be at the root of the zip OR one folder deep (MyScene/scene.json)
    ///   - All .cs files anywhere under scripts/ are auto-discovered
    ///   - Scripts do NOT need to be listed in scene.json — discovery is automatic
    ///   - If a script name is already declared in scene.json scripts[], the file on disk wins
    ///   - templates/ folder is auto-detected next to scene.json
    /// </summary>
    public static class ZipLoader
    {
        public record LoadResult(
            SceneGenConfig Config,
            string         ExtractDir,    // temp dir — caller must delete when done
            string         SceneJsonDir); // dir containing scene.json (for relative path resolution)

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// Extracts zipBytes to a temp directory and loads the scene config.
        /// Caller is responsible for deleting ExtractDir when finished.
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

            return LoadFromExtractedDir(extractDir, log);
        }

        /// <summary>
        /// Loads from an already-extracted directory.
        /// </summary>
        public static LoadResult LoadFromExtractedDir(string extractDir, Action<string>? log = null)
        {
            // ── Find scene.json ───────────────────────────────────────
            var sceneJsonPath = FindSceneJson(extractDir)
                ?? throw new FileNotFoundException(
                    "scene.json not found in the zip archive. " +
                    "Place scene.json at the root of the zip, e.g.: MyScene.zip/scene.json");

            var sceneJsonDir = Path.GetDirectoryName(sceneJsonPath)!;
            log?.Invoke($"[ZipLoader] Found scene.json at: {sceneJsonPath}");

            // ── Load config from scene.json ───────────────────────────
            SceneGenConfig cfg;
            try
            {
                cfg = JsonConvert.DeserializeObject<SceneGenConfig>(
                          File.ReadAllText(sceneJsonPath))
                      ?? throw new InvalidDataException("scene.json deserialised to null.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"scene.json parse error: {ex.Message}");
            }

            // ── Auto-discover scripts/*.cs ────────────────────────────
            var scriptsDir = Path.Combine(sceneJsonDir, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                // Build a set of script names already declared in scene.json
                var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in cfg.Scripts)
                    if (!string.IsNullOrWhiteSpace(s.Name))
                        declared.Add(s.Name);

                foreach (var csFile in Directory.EnumerateFiles(
                             scriptsDir, "*.cs", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(csFile);
                    if (declared.Contains(name))
                    {
                        // Already declared — update File path to the discovered absolute path
                        // so ProjectCreator can copy it
                        var existing = cfg.Scripts.Find(s =>
                            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (existing != null) existing.File = csFile;
                        log?.Invoke($"[ZipLoader]   script (update path) ← {name}.cs");
                    }
                    else
                    {
                        // Not declared — add it automatically
                        cfg.Scripts.Add(new ScriptConfig { Name = name, File = csFile });
                        declared.Add(name);
                        log?.Invoke($"[ZipLoader]   script (auto-discovered) ← {name}.cs");
                    }
                }
            }

            log?.Invoke($"[ZipLoader] Loaded: {cfg.Scenes.Count} scene(s), " +
                        $"{cfg.GameObjects.Count} object(s), {cfg.Scripts.Count} script(s).");

            return new LoadResult(cfg, extractDir, sceneJsonDir);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static string? FindSceneJson(string dir)
        {
            // 1. Direct: <dir>/scene.json
            var direct = Path.Combine(dir, "scene.json");
            if (File.Exists(direct)) return direct;

            // 2. One folder deep: <dir>/<anything>/scene.json
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var nested = Path.Combine(sub, "scene.json");
                if (File.Exists(nested)) return nested;
            }

            return null;
        }
    }
}
