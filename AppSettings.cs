using System;
using System.IO;
using Newtonsoft.Json;

namespace UnitySceneGen.Core
{
    public class AppSettings
    {
        /// <summary>
        /// Default Unity.exe path. Used when no path is saved and none is supplied by the caller.
        /// Shared by GUI and API server.
        /// </summary>
        public const string DefaultUnityExePath =
            @"C:\Program Files\Unity\Hub\Editor\6000.0.3f1\Editor\Unity.exe";

        // ── Persisted fields ──────────────────────────────────────────

        /// <summary>Last scene.zip path used in the GUI.</summary>
        public string? LastZipPath       { get; set; }

        /// <summary>Last Unity.exe path used.</summary>
        public string? LastUnityExePath  { get; set; }

        /// <summary>Last output directory used.</summary>
        public string? LastOutputDir     { get; set; }

        // ── Persistence ───────────────────────────────────────────────

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

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }

        // Keep static overload for any callers that pass an instance
        public static void Save(AppSettings s) => s.Save();
    }
}
