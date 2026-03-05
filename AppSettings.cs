using System;
using System.IO;
using Newtonsoft.Json;

namespace UnitySceneGen.Core
{
    public class AppSettings
    {
        /// <summary>
        /// Fallback Unity.exe path used when no path has been saved and
        /// none is supplied by the caller. Shared by WPF, CLI and API.
        /// </summary>
        public const string DefaultUnityExePath =
            @"C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe";

        public string? LastConfigPath { get; set; }
        public string? LastUnityExePath { get; set; }
        public string? LastOutputPath { get; set; }
        public bool ForceMode { get; set; }
        public int LastConfigTab { get; set; }  // 0 = File, 1 = Inline JSON

        private static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnitySceneGen", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                               File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonConvert.SerializeObject(s, Formatting.Indented));
            }
            catch { }
        }
    }
}