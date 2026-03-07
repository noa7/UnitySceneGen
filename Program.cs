using System;
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
            // ── Parse --port ──────────────────────────────────────────────────
            int? apiPort = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var p))
                    apiPort = p;

            // ── API-only mode: UnitySceneGen.exe --port 5782 ──────────────────
            if (apiPort.HasValue && args.Length <= 2)
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;

                var server = new ApiServer(apiPort.Value);
                server.Start();

                Console.WriteLine("Running in API-only mode. Press Ctrl+C to stop.");
                var done = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    server.Stop();
                    done.Set();
                };
                done.Wait();
                return 0;
            }

            // ── GUI mode: no arguments (or --port alongside GUI) ──────────────
            // Start the API server on a background thread if --port was given,
            // so the GUI and API run simultaneously.
            ApiServer? bgServer = null;
            if (apiPort.HasValue)
            {
                if (!AttachConsole(-1)) AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                bgServer = new ApiServer(apiPort.Value);
                bgServer.Start();
            }

            var app    = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(apiPort);
            app.MainWindow = window;
            window.Show();
            int result = app.Run();

            bgServer?.Stop();
            return result;
        }
    }
}
