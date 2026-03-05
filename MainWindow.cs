using System;
using System.Diagnostics;
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
    public class MainWindow : Window
    {
        // ── Controls ──────────────────────────────────────────────────
        private readonly TextBox _txtConfig;
        private readonly TextBox _txtInlineJson;
        private readonly TextBox _txtUnityExe;
        private readonly TextBox _txtOutput;
        private readonly CheckBox _chkForce;
        private readonly Button _btnGenerate;
        private readonly Button _btnOpenFolder;
        private readonly TextBox _txtLog;
        private readonly ScrollViewer _logScroller;
        private readonly Border _statusBar;
        private readonly TextBlock _txtStatus;
        private readonly TabControl _configTabs;

        // ── State ─────────────────────────────────────────────────────
        private AppSettings _settings = AppSettings.Load();
        private CancellationTokenSource? _cts;
        private string? _lastOutputPath;
        private string? _tempConfigPath;

        // ── Palette ───────────────────────────────────────────────────
        private static readonly SolidColorBrush C_Background = Brush("#252526");
        private static readonly SolidColorBrush C_Surface = Brush("#1E1E1E");
        private static readonly SolidColorBrush C_Surface2 = Brush("#2D2D30");
        private static readonly SolidColorBrush C_Border = Brush("#555555");
        private static readonly SolidColorBrush C_FgPrimary = Brush("#D4D4D4");
        private static readonly SolidColorBrush C_FgMuted = Brush("#858585");
        private static readonly SolidColorBrush C_Accent = Brush("#007ACC");
        private static readonly SolidColorBrush C_AccentHover = Brush("#1A8FDD");
        private static readonly SolidColorBrush C_BtnBg = Brush("#2D2D30");
        private static readonly SolidColorBrush C_BtnHover = Brush("#3E3E42");
        private static readonly SolidColorBrush C_Success = Brush("#28A745");
        private static readonly SolidColorBrush C_Error = Brush("#DC3545");
        private static readonly SolidColorBrush C_Warning = Brush("#FFC107");

        // ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            Title = "Unity Scene Generator";
            Width = 900;
            Height = 900;
            MinWidth = 720;
            MinHeight = 680;
            Background = C_Background;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            // ── Root DockPanel ────────────────────────────────────────
            var root = new DockPanel { Margin = new Thickness(18) };

            // ── Header ────────────────────────────────────────────────
            var header = new TextBlock
            {
                Text = "Unity Scene Generator",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#569CD6"),
                Margin = new Thickness(0, 0, 0, 16),
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // ── Status bar (bottom) ───────────────────────────────────
            _txtStatus = new TextBlock
            {
                Text = "Ready",
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(10, 5, 10, 5),
            };
            _statusBar = new Border
            {
                Background = C_Accent,
                CornerRadius = new CornerRadius(2),
                Child = _txtStatus,
                Margin = new Thickness(0, 8, 0, 0),
            };
            DockPanel.SetDock(_statusBar, Dock.Bottom);
            root.Children.Add(_statusBar);

            // ── Fixed top stack ───────────────────────────────────────
            var stack = new StackPanel();
            DockPanel.SetDock(stack, Dock.Top);
            root.Children.Add(stack);

            // ── Config section: tabbed File / Inline JSON ─────────────
            stack.Children.Add(MakeLabel("Config JSON"));

            _configTabs = new TabControl
            {
                Background = C_Surface2,
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(0),
            };

            // — Tab 1: File path —
            var fileTabContent = new StackPanel { Margin = new Thickness(8) };
            fileTabContent.Children.Add(MakeLabel("Path to .json config file"));
            fileTabContent.Children.Add(MakePathRow(out _txtConfig, "Browse…", BrowseConfig, "0,0,0,0"));

            var fileTab = new TabItem { Header = "  File  " };
            StyleTabItem(fileTab);
            fileTab.Content = fileTabContent;

            // — Tab 2: Inline JSON editor —
            _txtInlineJson = new TextBox
            {
                Background = C_Surface,
                Foreground = Brush("#CE9178"),
                CaretBrush = Brushes.White,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 200,
                Padding = new Thickness(6),
                Text = DefaultJsonTemplate(),
            };

            // Toolbar above editor
            var jsonToolbar = new Grid { Margin = new Thickness(8, 8, 8, 4) };
            jsonToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            jsonToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            jsonToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            jsonToolbar.Children.Add(new TextBlock
            {
                Text = "Paste or type your JSON directly — no file needed",
                Foreground = C_FgMuted,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var btnClear = MakeButton("Clear", C_BtnBg, C_BtnHover, width: 58, fontSize: 11);
            btnClear.Margin = new Thickness(6, 0, 6, 0);
            btnClear.Click += (_, __) => _txtInlineJson.Clear();
            Grid.SetColumn(btnClear, 1);
            jsonToolbar.Children.Add(btnClear);

            var btnLoadFile = MakeButton("Load from File…", C_BtnBg, C_BtnHover, fontSize: 11);
            btnLoadFile.Click += LoadJsonFromFile_Click;
            Grid.SetColumn(btnLoadFile, 2);
            jsonToolbar.Children.Add(btnLoadFile);

            var jsonTabContent = new StackPanel();
            jsonTabContent.Children.Add(jsonToolbar);
            jsonTabContent.Children.Add(new Border
            {
                Margin = new Thickness(8, 0, 8, 8),
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Child = _txtInlineJson,
            });

            var jsonTab = new TabItem { Header = "  Inline JSON  " };
            StyleTabItem(jsonTab);
            jsonTab.Content = jsonTabContent;

            _configTabs.Items.Add(fileTab);
            _configTabs.Items.Add(jsonTab);
            stack.Children.Add(_configTabs);

            // ── Unity exe ─────────────────────────────────────────────
            stack.Children.Add(MakeLabel("Unity Editor Executable (Unity.exe)"));
            stack.Children.Add(MakePathRow(out _txtUnityExe, "Browse…", BrowseUnity, "0,0,0,10"));

            // ── Output folder ─────────────────────────────────────────
            stack.Children.Add(MakeLabel("Output Folder"));
            stack.Children.Add(MakePathRow(out _txtOutput, "Browse…", BrowseOutput, "0,0,0,14"));

            // ── Options row ───────────────────────────────────────────
            _chkForce = new CheckBox
            {
                Content = "--force  (delete and regenerate from scratch)",
                Foreground = C_FgPrimary,
                Margin = new Thickness(0, 0, 0, 10),
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(_chkForce);

            // ── Action buttons ────────────────────────────────────────
            var btnRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _btnGenerate = MakeButton("▶  Generate", C_Accent, C_AccentHover,
                                      fontSize: 14, fontWeight: FontWeights.Bold);
            _btnGenerate.Click += Generate_Click;
            btnRow.Children.Add(_btnGenerate);

            _btnOpenFolder = MakeButton("Open Output Folder", C_BtnBg, C_BtnHover);
            _btnOpenFolder.IsEnabled = false;
            _btnOpenFolder.Click += (_, __) => OpenOutputFolder();
            _btnOpenFolder.Margin = new Thickness(10, 0, 0, 0);
            _btnOpenFolder.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_btnOpenFolder, 1);
            btnRow.Children.Add(_btnOpenFolder);
            stack.Children.Add(btnRow);

            // ── Log panel — last child fills remaining height ─────────
            _txtLog = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = Brush("#DCDCDC"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalAlignment = VerticalAlignment.Top,
            };

            _logScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _txtLog,
            };

            var logHeader = new TextBlock
            {
                Text = " Log Output",
                Background = C_Surface,
                Foreground = C_FgMuted,
                Padding = new Thickness(4, 3, 4, 3),
                FontFamily = new FontFamily("Consolas"),
            };

            var logInner = new DockPanel { Background = C_Surface, LastChildFill = true };
            DockPanel.SetDock(logHeader, Dock.Top);
            logInner.Children.Add(logHeader);
            logInner.Children.Add(_logScroller);

            root.Children.Add(new Border
            {
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Child = logInner,
                Margin = new Thickness(0, 0, 0, 8),
            });

            Content = root;
            Loaded += (_, __) => LoadSettings();
        }

        // ── Settings ──────────────────────────────────────────────────
        private void LoadSettings()
        {
            _txtConfig.Text = _settings.LastConfigPath ?? "";
            _txtUnityExe.Text = !string.IsNullOrEmpty(_settings.LastUnityExePath)
                ? _settings.LastUnityExePath
                : AppSettings.DefaultUnityExePath;
            _txtOutput.Text = _settings.LastOutputPath ?? "";
            _chkForce.IsChecked = _settings.ForceMode;
            _configTabs.SelectedIndex = _settings.LastConfigTab;
        }

        private void SaveSettings()
        {
            _settings.LastConfigPath = _txtConfig.Text.Trim();
            _settings.LastUnityExePath = _txtUnityExe.Text.Trim();
            _settings.LastOutputPath = _txtOutput.Text.Trim();
            _settings.ForceMode = _chkForce.IsChecked == true;
            _settings.LastConfigTab = _configTabs.SelectedIndex;
            AppSettings.Save(_settings);
        }

        // ── Browse handlers ───────────────────────────────────────────
        private void BrowseConfig(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            { Filter = "JSON Config|*.json|All Files|*.*", Title = "Select Config File" };
            if (dlg.ShowDialog() == true) _txtConfig.Text = dlg.FileName;
        }

        private void BrowseUnity(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            { Filter = "Unity Editor|Unity.exe|All Executables|*.exe", Title = "Select Unity.exe" };
            if (dlg.ShowDialog() == true) _txtUnityExe.Text = dlg.FileName;
        }

        private void BrowseOutput(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Output Folder",
                UseDescriptionForTitle = true,
                SelectedPath = _txtOutput.Text.Trim(),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _txtOutput.Text = dlg.SelectedPath;
        }

        // Loads a file into the inline editor from the "Load from File…" button
        private void LoadJsonFromFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            { Filter = "JSON Config|*.json|All Files|*.*", Title = "Load JSON into Editor" };
            if (dlg.ShowDialog() == true)
            {
                try { _txtInlineJson.Text = File.ReadAllText(dlg.FileName); }
                catch (Exception ex) { ShowError($"Could not read file: {ex.Message}"); }
            }
        }

        // ── Generate ──────────────────────────────────────────────────
        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var unityExe = _txtUnityExe.Text.Trim();
            var outputDir = _txtOutput.Text.Trim();
            var force = _chkForce.IsChecked == true;
            var useInline = _configTabs.SelectedIndex == 1;

            // Resolve which config source to use
            string configPath;
            if (useInline)
            {
                var json = _txtInlineJson.Text.Trim();
                if (string.IsNullOrEmpty(json))
                { ShowError("The Inline JSON editor is empty. Paste your config JSON first."); return; }

                try
                {
                    CleanTempConfig();
                    _tempConfigPath = Path.Combine(Path.GetTempPath(),
                        $"UnitySceneGen_{Guid.NewGuid():N}.json");
                    File.WriteAllText(_tempConfigPath, json);
                    configPath = _tempConfigPath;
                }
                catch (Exception ex)
                { ShowError($"Could not write temp config: {ex.Message}"); return; }
            }
            else
            {
                configPath = _txtConfig.Text.Trim();
                if (!File.Exists(configPath))
                { ShowError("Config file not found. Check the path or use the Inline JSON tab."); return; }
            }

            if (!File.Exists(unityExe))
            { ShowError("Unity executable not found."); return; }
            if (string.IsNullOrEmpty(outputDir))
            { ShowError("Output folder is required."); return; }

            SaveSettings();
            SetBusy(true);
            _txtLog.Text = "";
            _lastOutputPath = null;
            _btnOpenFolder.IsEnabled = false;

            _cts = new CancellationTokenSource();

            try
            {
                var opts = new GenerationOptions
                {
                    ConfigPath = configPath,
                    UnityExePath = unityExe,
                    OutputDir = outputDir,
                    Force = force,
                };

                var result = await Task.Run(
                    () => GenerationEngine.Run(opts, AppendLog, _cts.Token),
                    _cts.Token);

                if (result.Success)
                {
                    _lastOutputPath = result.ProjectPath;
                    _btnOpenFolder.IsEnabled = true;
                    SetStatus("✓ Generation complete!", C_Success);
                    AppendLog($"\n✓ Done — project at: {result.ProjectPath}");

                    // ── Auto-open Explorer in the generated project ───
                    OpenOutputFolder();
                }
                else
                {
                    SetStatus($"✗ {result.Error}", C_Error);
                    AppendLog($"\n✗ FAILED: {result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.", C_Warning);
                AppendLog("\n⚠ Generation cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus($"Unexpected error: {ex.Message}", C_Error);
                AppendLog($"\n✗ Exception: {ex}");
            }
            finally
            {
                SetBusy(false);
                CleanTempConfig();
            }
        }

        // ── Folder opener ─────────────────────────────────────────────
        private void OpenOutputFolder()
        {
            if (_lastOutputPath != null && Directory.Exists(_lastOutputPath))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _lastOutputPath,
                    UseShellExecute = true,
                });
        }

        // ── Temp file cleanup ─────────────────────────────────────────
        private void CleanTempConfig()
        {
            try
            {
                if (_tempConfigPath != null && File.Exists(_tempConfigPath))
                    File.Delete(_tempConfigPath);
            }
            catch { }
            _tempConfigPath = null;
        }

        // ── UI helpers ────────────────────────────────────────────────
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
                _txtStatus.Text = text;
                _statusBar.Background = color;
            });
        }

        private void SetBusy(bool busy)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _btnGenerate.IsEnabled = !busy;
                _btnGenerate.Content = busy ? "⏳ Generating…" : "▶  Generate";
                if (busy) SetStatus("Running…", C_Accent);
            });
        }

        private void ShowError(string msg) =>
            MessageBox.Show(msg, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);

        // ── Widget factories ──────────────────────────────────────────
        private static TextBlock MakeLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = C_FgPrimary,
            Margin = new Thickness(0, 0, 0, 3),
        };

        private static Grid MakePathRow(
            out TextBox txt, string btnLabel,
            RoutedEventHandler handler, string margin)
        {
            txt = new TextBox
            {
                Background = C_Surface,
                Foreground = C_FgPrimary,
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 4, 5, 4),
            };
            var btn = MakeButton(btnLabel, C_BtnBg, C_BtnHover, width: 80);
            btn.Click += handler;
            btn.Margin = new Thickness(6, 0, 0, 0);

            var g = new Grid { Margin = ThicknessFromString(margin) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(txt);
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
            return g;
        }

        private static Button MakeButton(
            string label, SolidColorBrush bg, SolidColorBrush hover,
            double width = double.NaN, double fontSize = 13,
            FontWeight? fontWeight = null)
        {
            var btn = new Button
            {
                Content = label,
                Background = bg,
                Foreground = Brushes.White,
                BorderBrush = C_Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                FontSize = fontSize,
                FontWeight = fontWeight ?? FontWeights.Normal,
            };
            if (!double.IsNaN(width)) btn.Width = width;
            btn.MouseEnter += (_, __) => btn.Background = hover;
            btn.MouseLeave += (_, __) => btn.Background = bg;
            btn.IsEnabledChanged += (_, a) => btn.Opacity = (bool)a.NewValue ? 1.0 : 0.4;
            return btn;
        }

        private static void StyleTabItem(TabItem tab)
        {
            tab.Foreground = C_FgPrimary;
            tab.Background = C_Surface2;
            tab.BorderBrush = C_Border;
            tab.BorderThickness = new Thickness(1, 1, 1, 0);
            tab.Padding = new Thickness(6, 4, 6, 4);
        }

        private static SolidColorBrush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private static Thickness ThicknessFromString(string s)
        {
            var p = Array.ConvertAll(s.Split(','), double.Parse);
            return p.Length == 4 ? new Thickness(p[0], p[1], p[2], p[3]) : new Thickness(p[0]);
        }

        // ── Default JSON pre-filled in inline editor ──────────────────
        private static string DefaultJsonTemplate() => @"{
  ""project"": {
    ""name"": ""MyUnityProject"",
    ""unityVersion"": ""2022.3.20f1"",
    ""packages"": [ ""com.unity.ugui"", ""com.unity.textmeshpro"" ]
  },
  ""settings"": {
    ""tags"": [ ""Player"", ""Enemy"" ],
    ""layers"": [ ""Gameplay"", ""UI"" ]
  },
  ""scenes"": [
    {
      ""name"": ""Main"",
      ""path"": ""Assets/Scenes/Main.unity"",
      ""roots"": [ ""go.root"" ]
    }
  ],
  ""gameObjects"": [
    {
      ""id"": ""go.root"",
      ""name"": ""Root"",
      ""active"": true,
      ""tag"": ""Untagged"",
      ""layer"": ""Default"",
      ""children"": [ ""go.camera"" ]
    },
    {
      ""id"": ""go.camera"",
      ""name"": ""Main Camera"",
      ""tag"": ""MainCamera"",
      ""components"": [
        { ""type"": ""UnityEngine.Camera"" },
        { ""type"": ""UnityEngine.AudioListener"" }
      ]
    }
  ]
}";
    }
}