using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class ProjectCreator
    {
        /// <summary>
        /// Creates or verifies the Unity project folder and writes all generated files.
        /// </summary>
        /// <param name="cfg">The fully-loaded and template-resolved config.</param>
        /// <param name="outputDir">Parent directory — project folder is created inside here.</param>
        /// <param name="configDir">
        /// Directory containing the original config or manifest file.
        /// Used to resolve relative script File paths.
        /// </param>
        /// <param name="force">When true, deletes and recreates the project folder.</param>
        /// <param name="log">Log sink.</param>
        public static string CreateOrVerify(
            SceneGenConfig cfg,
            string outputDir,
            string configDir,
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
            WriteScripts(cfg, projectRoot, configDir, log);
            WriteConfig(cfg, projectRoot, log);   // serialise merged config (not original file)

            return projectRoot;
        }

        // ── Scaffold ──────────────────────────────────────────────────

        private static void Scaffold(
            string root, SceneGenConfig cfg, string unityVersion, Action<string> log)
        {
            var dirs = new[]
            {
                "Assets",
                "Assets/Scenes",
                "Assets/Scripts",
                "Assets/Editor",
                "Assets/Editor/SceneGenerator",
                "Packages",
                "ProjectSettings",
                "Logs",
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

        // ── All 31 Unity built-in modules — used when scene.json specifies none ──
        // These are always available in every Unity install and are zero-cost to include.
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

            // Separate what the project declared into modules vs registry packages
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

            // ── Module policy ─────────────────────────────────────────
            // If the project declared any modules explicitly → use exactly those.
            // If none declared → include all 31 built-ins so any script compiles.
            var modules = declaredModules.Count > 0
                ? declaredModules
                : new Dictionary<string, string>(AllBuiltinModules);

            string moduleSource = declaredModules.Count > 0
                ? $"{declaredModules.Count} explicit"
                : $"all {AllBuiltinModules.Count} (default — none declared in scene.json)";

            // ── Always-present registry packages ──────────────────────
            // ugui and newtonsoft-json are required by the builder itself.
            registryPackages.TryAdd("com.unity.ugui", "1.0.0");
            registryPackages.TryAdd("com.unity.nuget.newtonsoft-json", "3.2.1");

            // ── Merge and write ───────────────────────────────────────
            var deps = new Dictionary<string, string>(modules);
            foreach (var kv in registryPackages)
                deps[kv.Key] = kv.Value;

            File.WriteAllText(
                Path.Combine(root, "Packages", "manifest.json"),
                JsonConvert.SerializeObject(new { dependencies = deps }, Formatting.Indented));

            log($"  Packages/manifest.json — {deps.Count} packages " +
                $"(modules: {moduleSource}, registry: {registryPackages.Count})");
        }

        // ── Scripts ───────────────────────────────────────────────────

        private static void WriteScripts(
            SceneGenConfig cfg, string root, string configDir, Action<string> log)
        {
            if (cfg.Scripts == null || cfg.Scripts.Count == 0) return;

            var dir = Path.Combine(root, "Assets", "Scripts");
            Directory.CreateDirectory(dir);

            foreach (var script in cfg.Scripts)
            {
                if (string.IsNullOrWhiteSpace(script.Name))
                {
                    log("[ProjectCreator] Skipping script with empty name.");
                    continue;
                }

                var dest = Path.Combine(dir, $"{script.Name}.cs");

                if (!string.IsNullOrWhiteSpace(script.File))
                {
                    // ── External .cs file — copy directly, no wrapping ──────
                    // Resolve relative paths against configDir
                    var sourcePath = Path.IsPathRooted(script.File)
                        ? script.File
                        : Path.GetFullPath(Path.Combine(configDir, script.File));

                    if (!File.Exists(sourcePath))
                        throw new FileNotFoundException(
                            $"Script file not found: '{sourcePath}' " +
                            $"(from '{script.File}' for script '{script.Name}')");

                    File.Copy(sourcePath, dest, overwrite: true);
                    log($"[ProjectCreator] Script (file)      → {dest}");
                }
                else
                {
                    // ── Inline body — generate MonoBehaviour wrapper ─────────
                    var src = GenerateScriptSource(script);
                    File.WriteAllText(dest, src);
                    log($"[ProjectCreator] Script (generated)  → {dest}");
                }
            }
        }

        /// <summary>
        /// Wraps the user-supplied class body in a proper MonoBehaviour file.
        /// If body is empty the generated class has stub Start/Update methods.
        /// </summary>
        private static string GenerateScriptSource(ScriptConfig script)
        {
            var body = string.IsNullOrWhiteSpace(script.Body)
                ? "    void Start() { }\n\n    void Update() { }"
                : script.Body;

            var classBlock =
                $"using UnityEngine;\n\n" +
                $"// Auto-generated by UnitySceneGen\n" +
                $"public class {script.Name} : MonoBehaviour\n" +
                $"{{\n{body}\n}}\n";

            if (!string.IsNullOrWhiteSpace(script.Namespace))
                classBlock =
                    $"using UnityEngine;\n\n" +
                    $"// Auto-generated by UnitySceneGen\n" +
                    $"namespace {script.Namespace}\n{{\n" +
                    $"public class {script.Name} : MonoBehaviour\n" +
                    $"{{\n{body}\n}}\n}}\n";

            return classBlock;
        }

        // ── Config ────────────────────────────────────────────────────

        /// <summary>
        /// Writes the merged, template-resolved SceneGenConfig as SceneGenConfig.json
        /// in the project root. Unity's Builder.cs reads this file — it must contain
        /// the fully-expanded config, not the original manifest.
        /// </summary>
        private static void WriteConfig(SceneGenConfig cfg, string root, Action<string> log)
        {
            var dest = Path.Combine(root, "SceneGenConfig.json");
            File.WriteAllText(dest,
                JsonConvert.SerializeObject(cfg, Formatting.Indented));
            log($"[ProjectCreator] SceneGenConfig.json → {dest}");
        }

        // ── Builder script ────────────────────────────────────────────

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

            var adjacent = Path.Combine(
                Path.GetDirectoryName(asm.Location)!, "Builder.cs");
            if (File.Exists(adjacent))
                return File.ReadAllText(adjacent);

            throw new FileNotFoundException(
                $"Builder script not found. Expected embedded resource '{resource}' " +
                $"or a file called Builder.cs next to the executable.");
        }
    }
}