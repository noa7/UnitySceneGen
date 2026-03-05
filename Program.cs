using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace UnitySceneGen
{
    public static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        public static int Main(string[] args)
        {
            // ── NEW: parse --port flag ────────────────────────────────────────
            // Accepted anywhere in args: --port 5782
            // Does not interfere with any existing CLI flags.
            int? apiPort = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var p))
                    apiPort = p;

            // ── NEW: start API server if --port was supplied ──────────────────
            ApiServer? apiServer = null;
            if (apiPort.HasValue)
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                apiServer = new ApiServer(apiPort.Value);
                apiServer.Start();
            }

            // ── Existing: decide CLI vs WPF ───────────────────────────────────
            // Strip --port <n> from args before the CLI check so CliRunner
            // never sees an unknown flag.
            var cleanArgs = StripPortArgs(args);

            bool isCli = cleanArgs.Length > 0
                      && cleanArgs.Any(a => a.StartsWith("-"));

            if (isCli)
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                int code = CliRunner.Run(cleanArgs);
                apiServer?.Stop();
                return code;
            }

            // ── NEW: pure server mode (--port only, no other flags) ───────────
            if (apiPort.HasValue)
            {
                Console.WriteLine("Running in API-only mode. Press Ctrl+C to stop.");
                var done = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;        // prevent immediate process kill
                    apiServer?.Stop();
                    done.Set();
                };
                done.Wait();
                return 0;
            }

            // ── Existing: WPF mode (API server runs silently in background) ───
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            app.MainWindow = window;
            window.Show();
            int result = app.Run();
            apiServer?.Stop();
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a copy of args with "--port &lt;value&gt;" removed so existing
        /// CLI parsing never sees an unknown flag.
        /// </summary>
        private static string[] StripPortArgs(string[] args)
        {
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Length)
                {
                    i++; // skip the value too
                    continue;
                }
                list.Add(args[i]);
            }
            return list.ToArray();
        }
    }
}