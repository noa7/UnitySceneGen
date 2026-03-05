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
        public static string CreateOrVerify(
            SceneGenConfig cfg,
            string outputDir,
            string configPath,
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

            // Always refresh builder script, user scripts, and config so changes take effect
            WriteBuilderScript(projectRoot, log);
            WriteScripts(cfg, projectRoot, log);
            CopyConfig(configPath, projectRoot, log);

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

            // ProjectVersion.txt — tells Unity which editor version owns this project
            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                $"m_EditorVersion: {unityVersion}\n");

            WriteManifest(root, cfg, log);
        }

        private static void WriteManifest(string root, SceneGenConfig cfg, Action<string> log)
        {
            var deps = new Dictionary<string, string>
            {
                ["com.unity.modules.ai"] = "1.0.0",
                ["com.unity.modules.animation"] = "1.0.0",
                ["com.unity.modules.audio"] = "1.0.0",
                ["com.unity.modules.imgui"] = "1.0.0",
                ["com.unity.modules.physics"] = "1.0.0",
                ["com.unity.modules.ui"] = "1.0.0",
                ["com.unity.modules.unityanalytics"] = "1.0.0",
                ["com.unity.ugui"] = "1.0.0",
                ["com.unity.textmeshpro"] = "3.0.6",
                ["com.unity.nuget.newtonsoft-json"] = "3.2.1",
            };

            if (cfg.Project?.Packages != null)
                foreach (var pkg in cfg.Project.Packages)
                {
                    var parts = pkg.Split('@');
                    deps[parts[0]] = parts.Length > 1 ? parts[1] : "1.0.0";
                }

            File.WriteAllText(
                Path.Combine(root, "Packages", "manifest.json"),
                JsonConvert.SerializeObject(new { dependencies = deps }, Formatting.Indented));

            log($"  manifest.json — {deps.Count} packages");
        }

        // ── User scripts ──────────────────────────────────────────────

        private static void WriteScripts(SceneGenConfig cfg, string root, Action<string> log)
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

                var src = GenerateScriptSource(script);
                var dest = Path.Combine(dir, $"{script.Name}.cs");
                File.WriteAllText(dest, src);
                log($"[ProjectCreator] Script → {dest}");
            }
        }

        /// <summary>
        /// Wraps the user-supplied class body in a proper MonoBehaviour file.
        /// If the body is empty the generated class has stub Start/Update methods.
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

        private static void CopyConfig(string configPath, string root, Action<string> log)
        {
            var dest = Path.Combine(root, "SceneGenConfig.json");
            File.Copy(configPath, dest, overwrite: true);
            log($"[ProjectCreator] Config → {dest}");
        }

        private static string ExtractBuilderSource()
        {
            const string resource = "UnitySceneGen.UnityBuilder.Builder.cs";
            var asm = Assembly.GetExecutingAssembly();

            using var stream = asm.GetManifestResourceStream(resource);
            if (stream != null)
                using (var sr = new StreamReader(stream))
                    return sr.ReadToEnd();

            // Dev fallback: Builder.cs next to the exe
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