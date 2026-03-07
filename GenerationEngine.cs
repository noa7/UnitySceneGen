using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class GenerationEngine
    {
        /// <summary>
        /// Full generation pipeline.
        ///
        /// Input:  raw bytes of a scene zip (scene.json + optional scripts/ folder)
        /// Output: GenerationResult with ProjectPath pointing to the generated Unity project
        ///
        /// Steps:
        ///   1. Extract zip + load scene.json + auto-discover scripts
        ///   2. Resolve templates
        ///   3. Validate config
        ///   4. Scaffold Unity project folder
        ///   5. Unity Pass 1 — package import + compile
        ///   6. Unity Pass 2 — scene build
        /// </summary>
        public static GenerationResult Run(
            byte[]            zipBytes,
            string            unityExePath,
            string            outputDir,
            bool              force,
            Action<string>    log,
            CancellationToken ct)
        {
            string? extractDir = null;

            try
            {
                // ── 1. Extract zip + load config ──────────────────────
                log("=== Step 1/6: Loading scene zip ===");

                ZipLoader.LoadResult loaded;
                try
                {
                    loaded     = ZipLoader.Load(zipBytes, log);
                    extractDir = loaded.ExtractDir;
                }
                catch (Exception ex)
                {
                    return Fail($"Zip load error: {ex.Message}");
                }

                var cfg = loaded.Config;

                // ── 2. Resolve templates ───────────────────────────────
                log("=== Step 2/6: Resolving templates ===");

                var templateWarnings = new List<string>();
                var templatesDir     = Path.Combine(loaded.SceneJsonDir, "templates");

                TemplateResolver.Resolve(
                    cfg,
                    Directory.Exists(templatesDir) ? templatesDir : null,
                    templateWarnings);

                foreach (var w in templateWarnings) log($"  ⚠  {w}");
                ct.ThrowIfCancellationRequested();

                // ── 3. Validate ────────────────────────────────────────
                log("=== Step 3/6: Validating config ===");

                var validation = ConfigValidator.ValidateConfig(cfg);
                foreach (var w in validation.Warnings) log($"  ⚠  {w}");

                if (!validation.Valid)
                {
                    foreach (var e in validation.Errors) log($"  ✗  {e}");
                    return Fail(
                        $"Config validation failed with {validation.Errors.Count} error(s): " +
                        string.Join("; ", validation.Errors));
                }
                log("  ✓  Config valid.");
                ct.ThrowIfCancellationRequested();

                // ── 4. Scaffold project ────────────────────────────────
                log("=== Step 4/6: Setting up project folder ===");

                string projectPath;
                try
                {
                    projectPath = ProjectCreator.CreateOrVerify(
                        cfg,
                        outputDir,
                        loaded.SceneJsonDir,   // scripts resolved relative to scene.json dir
                        force,
                        log);
                }
                catch (Exception ex)
                {
                    return Fail($"Project creation error: {ex.Message}");
                }
                log($"  ✓  Project folder ready: {projectPath}");
                ct.ThrowIfCancellationRequested();

                // ── 5. Unity Pass 1 — import + compile ────────────────
                log("=== Step 5/6: Unity Pass 1 — Package import & compile ===");

                var pass1 = UnityLauncher.Pass1ImportAsync(
                    unityExePath, projectPath, log, ct).GetAwaiter().GetResult();

                if (!pass1.ok)
                    return Fail($"Unity Pass 1 failed: {pass1.error}");

                log("  ✓  Pass 1 complete.");
                ct.ThrowIfCancellationRequested();

                // ── 6. Unity Pass 2 — build scene ─────────────────────
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
                    return Fail($"Unity Pass 2 launch error: {ex.Message}");
                }

                foreach (var w in builderResult.Warnings) log($"  ⚠  {w}");

                if (!builderResult.Success)
                    return Fail($"Unity Builder failed: {builderResult.Error}");

                log("  ✓  Scene(s) generated.");

                // ── Write summary ──────────────────────────────────────
                var summaryPath = Path.Combine(projectPath, "SceneGenOutput.json");
                File.WriteAllText(summaryPath, JsonConvert.SerializeObject(new
                {
                    projectPath,
                    scenes      = builderResult.Scenes,
                    warnings    = builderResult.Warnings,
                    generatedAt = DateTime.UtcNow.ToString("o"),
                }, Formatting.Indented));

                return new GenerationResult
                {
                    Success            = true,
                    ProjectPath        = projectPath,
                    SceneGenOutputPath = summaryPath,
                    Warnings           = builderResult.Warnings,
                };
            }
            finally
            {
                // Cleanup extracted zip temp dir
                if (extractDir != null)
                    try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }

        // ── Validate-only ─────────────────────────────────────────────

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
                    loaded     = ZipLoader.Load(zipBytes);
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
                var templatesDir     = Path.Combine(loaded.SceneJsonDir, "templates");

                TemplateResolver.Resolve(
                    loaded.Config,
                    Directory.Exists(templatesDir) ? templatesDir : null,
                    templateWarnings);

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
}
