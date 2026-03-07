using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnitySceneGen.Core;

namespace UnitySceneGen
{
    /// <summary>
    /// Simple GUI for testing UnitySceneGen.
    /// Pick a scene.zip, set the Unity exe and output folder, click Generate.
    /// The GUI calls GenerationEngine directly — no HTTP round-trip needed.
    ///
    /// Once testing is complete this window is no longer needed;
    /// everything runs via the API server (POST /generate).
    /// </summary>
    public class MainWindow : Window
    {
        // ── Controls ──────────────────────────────────────────────────
        private readonly TextBox  _txtZip;
        private readonly TextBox  _txtUnityExe;
        private readonly TextBox  _txtOutput;
        private readonly CheckBox _chkForce;
        private readonly Button   _btnGenerate;
        private readonly Button   _btnOpenFolder;
        private readonly TextBox  _txtLog;
        private readonly ScrollViewer _logScroller;
        private readonly Border   _statusBar;
        private readonly TextBlock _txtStatus;

        // ── State ─────────────────────────────────────────────────────
        private readonly AppSettings _settings = AppSettings.Load();
        private CancellationTokenSource? _cts;
        private string? _lastOutputPath;

        // ── Palette ───────────────────────────────────────────────────
        private static readonly SolidColorBrush C_Bg       = B("#252526");
        private static readonly SolidColorBrush C_Surface  = B("#1E1E1E");
        private static readonly SolidColorBrush C_Surface2 = B("#2D2D30");
        private static readonly SolidColorBrush C_Border   = B("#555555");
        private static readonly SolidColorBrush C_FgPrimary= B("#D4D4D4");
        private static readonly SolidColorBrush C_FgMuted  = B("#858585");
        private static readonly SolidColorBrush C_Accent   = B("#007ACC");
        private static readonly SolidColorBrush C_BtnBg    = B("#2D2D30");
        private static readonly SolidColorBrush C_BtnHover = B("#3E3E42");
        private static readonly SolidColorBrush C_Success  = B("#28A745");
        private static readonly SolidColorBrush C_Error    = B("#DC3545");
        private static readonly SolidColorBrush C_Warning  = B("#FFC107");

        // ─────────────────────────────────────────────────────────────
        public MainWindow(int? apiPort = null)
        {
            Title  = "Unity Scene Generator" + (apiPort.HasValue ? $"  [API: {apiPort}]" : "");
            Width  = 820;
            Height = 760;
            MinWidth  = 640;
            MinHeight = 560;
            Background = C_Bg;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize   = 13;

            var root = new DockPanel { Margin = new Thickness(18) };

            // ── Header ────────────────────────────────────────────────
            var header = new TextBlock
            {
                Text       = "Unity Scene Generator",
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
                Foreground = B("#569CD6"),
                Margin     = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var subHeader = new TextBlock
            {
                Text       = "Send a scene.zip → receive a generated Unity project.zip",
                Foreground = C_FgMuted,
                FontSize   = 11,
                Margin     = new Thickness(0, 0, 0, 16),
            };
            DockPanel.SetDock(subHeader, Dock.Top);
            root.Children.Add(subHeader);

            // ── Status bar (bottom) ───────────────────────────────────
            _txtStatus = new TextBlock
            {
                Text      = "Ready — pick a scene.zip to get started",
                Foreground = Brushes.White,
                FontSize   = 12,
                Padding    = new Thickness(10, 5, 10, 5),
            };
            _statusBar = new Border
            {
                Background   = C_Accent,
                CornerRadius = new CornerRadius(2),
                Child        = _txtStatus,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            DockPanel.SetDock(_statusBar, Dock.Bottom);
            root.Children.Add(_statusBar);

            // ── Fixed top stack ───────────────────────────────────────
            var stack = new StackPanel();
            DockPanel.SetDock(stack, Dock.Top);
            root.Children.Add(stack);

            // ── Scene zip ─────────────────────────────────────────────
            stack.Children.Add(Label("scene.zip  (contains scene.json + optional scripts/ folder)"));
            stack.Children.Add(PathRow(out _txtZip, "Browse…", BrowseZip, "0,0,0,10"));
            _txtZip.Text = _settings.LastZipPath ?? "";

            // ── Unity exe ─────────────────────────────────────────────
            stack.Children.Add(Label("Unity Editor Executable  (Unity.exe)"));
            stack.Children.Add(PathRow(out _txtUnityExe, "Browse…", BrowseUnity, "0,0,0,10"));
            _txtUnityExe.Text = _settings.LastUnityExePath ?? AppSettings.DefaultUnityExePath;

            // ── Output folder ─────────────────────────────────────────
            stack.Children.Add(Label("Output Folder"));
            stack.Children.Add(PathRow(out _txtOutput, "Browse…", BrowseOutput, "0,0,0,14"));
            _txtOutput.Text = _settings.LastOutputDir ?? "";

            // ── Force checkbox ────────────────────────────────────────
            _chkForce = new CheckBox
            {
                Content   = "--force  (delete and regenerate from scratch)",
                Foreground = C_FgPrimary,
                Margin    = new Thickness(0, 0, 0, 14),
            };
            stack.Children.Add(_chkForce);

            // ── Buttons ───────────────────────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 14),
            };

            _btnGenerate = Btn("▶  Generate", C_Accent, C_Accent, fontSize: 14, bold: true);
            _btnGenerate.Width   = 150;
            _btnGenerate.Click  += GenerateAsync_Click;
            btnRow.Children.Add(_btnGenerate);

            var btnCancel = Btn("Cancel", C_BtnBg, C_BtnHover);
            btnCancel.Margin  = new Thickness(8, 0, 0, 0);
            btnCancel.Click  += (_, __) => _cts?.Cancel();
            btnRow.Children.Add(btnCancel);

            _btnOpenFolder = Btn("Open Output Folder", C_BtnBg, C_BtnHover);
            _btnOpenFolder.Margin    = new Thickness(8, 0, 0, 0);
            _btnOpenFolder.IsEnabled = false;
            _btnOpenFolder.Click    += (_, __) => OpenFolder();
            btnRow.Children.Add(_btnOpenFolder);

            // API hint (shown when running with --port)
            if (apiPort.HasValue)
            {
                var apiHint = new TextBlock
                {
                    Text       = $"  ●  API running on port {apiPort}",
                    Foreground = B("#28A745"),
                    FontSize   = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin     = new Thickness(16, 0, 0, 0),
                };
                btnRow.Children.Add(apiHint);
            }

            stack.Children.Add(btnRow);

            // ── Log ───────────────────────────────────────────────────
            stack.Children.Add(Label("Log"));

            _txtLog = new TextBox
            {
                Background  = C_Surface,
                Foreground  = C_FgPrimary,
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                FontFamily  = new FontFamily("Consolas"),
                FontSize    = 11,
                IsReadOnly  = true,
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding     = new Thickness(6),
            };

            _logScroller = new ScrollViewer
            {
                Content = _txtLog,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            // Log expands to fill remaining space
            var logBorder = new Border
            {
                BorderBrush     = C_Border,
                BorderThickness = new Thickness(1),
                Child           = _logScroller,
            };
            DockPanel.SetDock(logBorder, Dock.Bottom);
            root.Children.Add(logBorder);

            Content = root;
        }

        // ── Browse handlers ───────────────────────────────────────────

        private void BrowseZip(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select scene.zip",
                Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true) _txtZip.Text = dlg.FileName;
        }

        private void BrowseUnity(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Unity.exe",
                Filter = "Executable (*.exe)|*.exe",
            };
            if (dlg.ShowDialog() == true) _txtUnityExe.Text = dlg.FileName;
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select output folder",
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _txtOutput.Text = dlg.SelectedPath;
        }

        // ── Generate ──────────────────────────────────────────────────

        private async void GenerateAsync_Click(object sender, RoutedEventArgs e)
        {
            // ── Validate inputs ───────────────────────────────────────
            var zipPath    = _txtZip.Text.Trim();
            var unityExe   = _txtUnityExe.Text.Trim();
            var outputDir  = _txtOutput.Text.Trim();

            if (!File.Exists(zipPath))
            { Error("scene.zip not found. Browse to your zip file first."); return; }
            if (!File.Exists(unityExe))
            { Error("Unity.exe not found. Check the Unity executable path."); return; }
            if (string.IsNullOrWhiteSpace(outputDir))
            { Error("Output folder is required."); return; }

            // ── Save settings ─────────────────────────────────────────
            _settings.LastZipPath    = zipPath;
            _settings.LastUnityExePath = unityExe;
            _settings.LastOutputDir  = outputDir;
            _settings.Save();

            // ── Start generation ──────────────────────────────────────
            SetBusy(true);
            _txtLog.Clear();
            _btnOpenFolder.IsEnabled = false;
            _cts = new CancellationTokenSource();

            try
            {
                var zipBytes  = await File.ReadAllBytesAsync(zipPath);
                var force     = _chkForce.IsChecked == true;

                var result = await Task.Run(() =>
                    GenerationEngine.Run(
                        zipBytes, unityExe, outputDir, force,
                        line => AppendLog(line),
                        _cts.Token));

                if (result.Success)
                {
                    _lastOutputPath = result.ProjectPath;
                    _btnOpenFolder.IsEnabled = true;
                    SetStatus("✓  Generation complete!", C_Success);
                    AppendLog($"\n✓  Done — project at: {result.ProjectPath}");
                }
                else
                {
                    SetStatus($"✗  {result.Error}", C_Error);
                    AppendLog($"\n✗  FAILED: {result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.", C_Warning);
                AppendLog("\n⚠  Generation cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus($"Unexpected error: {ex.Message}", C_Error);
                AppendLog($"\n✗  Exception: {ex}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private void OpenFolder()
        {
            if (_lastOutputPath != null && Directory.Exists(_lastOutputPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName       = "explorer.exe",
                    Arguments      = _lastOutputPath,
                    UseShellExecute = true,
                });
        }

        private void AppendLog(string line)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _txtLog.AppendText(line + "\n");
                _logScroller.ScrollToBottom();
            });
        }

        private void SetStatus(string text, SolidColorBrush color)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _txtStatus.Text        = text;
                _statusBar.Background  = color;
            });
        }

        private void SetBusy(bool busy)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _btnGenerate.IsEnabled = !busy;
                _btnGenerate.Content   = busy ? "⏳ Generating…" : "▶  Generate";
                if (busy) SetStatus("Running…", C_Accent);
            });
        }

        private void Error(string msg) =>
            MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);

        // ── Widget factories ──────────────────────────────────────────

        private static TextBlock Label(string text) => new TextBlock
        {
            Text       = text,
            Foreground = C_FgPrimary,
            Margin     = new Thickness(0, 0, 0, 3),
        };

        private static Grid PathRow(
            out TextBox txt, string btnLabel,
            RoutedEventHandler handler, string margin)
        {
            txt = new TextBox
            {
                Background      = C_Surface,
                Foreground      = C_FgPrimary,
                BorderBrush     = C_Border,
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(5, 4, 5, 4),
            };
            var btn = Btn(btnLabel, C_BtnBg, C_BtnHover, width: 80);
            btn.Margin  = new Thickness(6, 0, 0, 0);
            btn.Click  += handler;

            var g = new Grid { Margin = ParseMargin(margin) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(txt);
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
            return g;
        }

        private static Button Btn(
            string label, SolidColorBrush bg, SolidColorBrush hover,
            double width = double.NaN, double fontSize = 13, bool bold = false)
        {
            var btn = new Button
            {
                Content         = label,
                Background      = bg,
                Foreground      = Brushes.White,
                BorderBrush     = C_Border,
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(12, 5, 12, 5),
                Cursor          = Cursors.Hand,
                FontSize        = fontSize,
                FontWeight      = bold ? FontWeights.Bold : FontWeights.Normal,
            };
            if (!double.IsNaN(width)) btn.Width = width;
            btn.MouseEnter      += (_, __) => btn.Background = hover;
            btn.MouseLeave      += (_, __) => btn.Background = bg;
            btn.IsEnabledChanged += (_, a)  => btn.Opacity   = (bool)a.NewValue ? 1.0 : 0.4;
            return btn;
        }

        private static SolidColorBrush B(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private static Thickness ParseMargin(string s)
        {
            var p = Array.ConvertAll(s.Split(','), double.Parse);
            return p.Length == 4 ? new Thickness(p[0], p[1], p[2], p[3]) : new Thickness(p[0]);
        }
    }
}
