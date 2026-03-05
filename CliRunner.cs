using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    public static class CliRunner
    {
        public static int Run(string[] args)
        {
            Console.WriteLine("UnitySceneGen — Unity Scene Generator");
            Console.WriteLine("======================================");

            if (args.Contains("--help") || args.Contains("-h"))
            {
                PrintHelp();
                return 0;
            }

            // ── Parse args ────────────────────────────────────────────
            var opts = new GenerationOptions();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config":  opts.ConfigPath   = Peek(args, i++); break;
                    case "--unity":   opts.UnityExePath = Peek(args, i++); break;
                    case "--out":     opts.OutputDir    = Peek(args, i++); break;
                    case "--project": opts.ProjectName  = Peek(args, i++); break;
                    case "--scene":   opts.SceneName    = Peek(args, i++); break;
                    case "--force":   opts.Force        = true;             break;
                }
            }

            // ── Validate required args ────────────────────────────────
            var missing = new List<string>();
            if (string.IsNullOrEmpty(opts.ConfigPath))   missing.Add("--config");
            if (string.IsNullOrEmpty(opts.UnityExePath)) missing.Add("--unity");
            if (string.IsNullOrEmpty(opts.OutputDir))    missing.Add("--out");

            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"✗  Missing required argument(s): {string.Join(", ", missing)}");
                Console.Error.WriteLine("   Run with --help for usage.");
                return 1;
            }

            if (!File.Exists(opts.ConfigPath))
            {
                Console.Error.WriteLine($"✗  Config not found: {opts.ConfigPath}");
                return 1;
            }

            if (!File.Exists(opts.UnityExePath))
            {
                Console.Error.WriteLine($"✗  Unity executable not found: {opts.UnityExePath}");
                return 1;
            }

            // ── Run ───────────────────────────────────────────────────
            Console.WriteLine();
            GenerationResult result;
            try
            {
                result = GenerationEngine.Run(opts, Console.WriteLine,
                    System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"✗  Unexpected exception: {ex}");
                return 3;
            }

            Console.WriteLine();
            if (result.Success)
            {
                Console.WriteLine("SUCCESS");
                Console.WriteLine($"  Project : {result.ProjectPath}");
                Console.WriteLine($"  Summary : {result.SceneGenOutputPath}");
                foreach (var w in result.Warnings)
                    Console.WriteLine($"  WARNING : {w}");
                return 0;
            }

            Console.Error.WriteLine($"FAILED: {result.Error}");
            return MapExitCode(result.Error);
        }

        private static string Peek(string[] args, int i) =>
            i + 1 < args.Length ? args[i + 1] : "";

        private static int MapExitCode(string error)
        {
            if (error.Contains("validation",    StringComparison.OrdinalIgnoreCase)) return 1;
            if (error.Contains("creation",      StringComparison.OrdinalIgnoreCase)) return 2;
            if (error.Contains("Pass 1",        StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Pass 2 launch", StringComparison.OrdinalIgnoreCase)) return 3;
            if (error.Contains("Builder",       StringComparison.OrdinalIgnoreCase)) return 4;
            return 3;
        }

        private static void PrintHelp() => Console.WriteLine(@"
Usage:
  UnitySceneGen.exe --config <path> --unity <Unity.exe> --out <dir> [options]

Required:
  --config <path>    Path to scene config JSON file
  --unity  <path>    Path to Unity Editor executable (Unity.exe)
  --out    <dir>     Output directory for the Unity project

Options:
  --force            Delete and recreate project from scratch
  --project <name>   Override project name from config
  --scene   <name>   Override scene name from config
  --help             Show this help

Exit Codes:
  0   Success
  1   Config validation error
  2   Project IO / folder creation error
  3   Unity batchmode execution error
  4   Unity scene build script error

Example:
  UnitySceneGen.exe ^
    --config C:\Configs\MyScene.json ^
    --unity  ""C:\Program Files\Unity\Hub\Editor\2022.3.20f1\Editor\Unity.exe"" ^
    --out    D:\UnityProjects
");
    }
}
