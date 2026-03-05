using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class GenerationEngine
    {
        public static GenerationResult Run(
            GenerationOptions opts,
            Action<string> log,
            CancellationToken ct)
        {
            // ── 1. Validate config ─────────────────────────────────────
            log("=== Step 1/5: Validating config ===");
            var validation = ConfigValidator.Validate(opts.ConfigPath);

            foreach (var w in validation.Warnings) log($"  ⚠  {w}");

            if (!validation.Valid)
            {
                foreach (var e in validation.Errors) log($"  ✗  {e}");
                return Fail($"Config validation failed with {validation.Errors.Count} error(s).");
            }
            log("  ✓  Config valid.");

            var cfg = validation.Config!;

            // Override name/scene from CLI flags if provided
            if (!string.IsNullOrEmpty(opts.ProjectName) && cfg.Project != null)
                cfg.Project.Name = opts.ProjectName;

            ct.ThrowIfCancellationRequested();

            // ── 2. Create project scaffold ─────────────────────────────
            log("=== Step 2/5: Setting up project folder ===");
            string projectPath;
            try
            {
                projectPath = ProjectCreator.CreateOrVerify(
                    cfg, opts.OutputDir, opts.ConfigPath, opts.Force, log);
            }
            catch (Exception ex)
            {
                return Fail($"Project creation error: {ex.Message}");
            }
            log($"  ✓  Project folder ready: {projectPath}");
            ct.ThrowIfCancellationRequested();

            // ── 3. Unity Pass 1 — import + compile ────────────────────
            log("=== Step 3/5: Unity Pass 1 — Package import & compile ===");

            var pass1 = UnityLauncher.Pass1ImportAsync(
                opts.UnityExePath, projectPath, log, ct).GetAwaiter().GetResult();

            if (!pass1.ok)
                return Fail($"Unity Pass 1 failed: {pass1.error}");

            log("  ✓  Pass 1 complete.");
            ct.ThrowIfCancellationRequested();

            // ── 4. Unity Pass 2 — build scene ─────────────────────────
            log("=== Step 4/5: Unity Pass 2 — Scene generation ===");

            BuilderResult builderResult;
            try
            {
                builderResult = UnityLauncher.Pass2BuildAsync(
                    opts.UnityExePath, projectPath, log, ct).GetAwaiter().GetResult();
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
            ct.ThrowIfCancellationRequested();

            // ── 5. Write output summary ────────────────────────────────
            log("=== Step 5/5: Writing output summary ===");

            var summary = new
            {
                projectPath,
                scenes      = builderResult.Scenes,
                warnings    = builderResult.Warnings,
                generatedAt = DateTime.UtcNow.ToString("o"),
            };

            var summaryPath = Path.Combine(projectPath, "SceneGenOutput.json");
            File.WriteAllText(summaryPath,
                JsonConvert.SerializeObject(summary, Formatting.Indented));

            log($"  ✓  Summary → {summaryPath}");

            return new GenerationResult
            {
                Success            = true,
                ProjectPath        = projectPath,
                SceneGenOutputPath = summaryPath,
                Warnings           = builderResult.Warnings,
            };
        }

        private static GenerationResult Fail(string error) =>
            new GenerationResult { Success = false, Error = error };
    }
}
