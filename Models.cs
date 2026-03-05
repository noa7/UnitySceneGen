using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnitySceneGen.Core
{
    // ─────────────────────────────────────────────────────────────────
    // Config models
    // ─────────────────────────────────────────────────────────────────

    public class SceneGenConfig
    {
        [JsonProperty("project")] public ProjectConfig? Project { get; set; }
        [JsonProperty("settings")] public SettingsConfig? Settings { get; set; }
        [JsonProperty("scripts")] public List<ScriptConfig> Scripts { get; set; } = new();
        [JsonProperty("scenes")] public List<SceneConfig> Scenes { get; set; } = new();
        [JsonProperty("gameObjects")] public List<GameObjectConfig> GameObjects { get; set; } = new();
    }

    public class ScriptConfig
    {
        /// <summary>File name without extension, e.g. "PlayerController".</summary>
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>
        /// Optional namespace. When set the generated file wraps the class in
        /// "namespace &lt;Namespace&gt; { ... }". Leave empty for no namespace.
        /// </summary>
        [JsonProperty("namespace")] public string Namespace { get; set; } = "";
        /// <summary>
        /// Raw C# source for the class body (everything inside the class braces).
        /// UnitySceneGen wraps it in a proper MonoBehaviour class automatically.
        /// </summary>
        [JsonProperty("body")] public string Body { get; set; } = "";
    }

    public class ProjectConfig
    {
        [JsonProperty("name")] public string Name { get; set; } = "MyProject";
        [JsonProperty("unityVersion")] public string UnityVersion { get; set; } = "2022.3.20f1";
        [JsonProperty("packages")] public List<string> Packages { get; set; } = new();
    }

    public class SettingsConfig
    {
        [JsonProperty("tags")] public List<string> Tags { get; set; } = new();
        [JsonProperty("layers")] public List<string> Layers { get; set; } = new();
    }

    public class SceneConfig
    {
        [JsonProperty("name")] public string Name { get; set; } = "Main";
        [JsonProperty("path")] public string Path { get; set; } = "Assets/Scenes/Main.unity";
        [JsonProperty("roots")] public List<string> Roots { get; set; } = new();
    }

    public class GameObjectConfig
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "GameObject";
        [JsonProperty("active")] public bool Active { get; set; } = true;
        [JsonProperty("tag")] public string Tag { get; set; } = "Untagged";
        [JsonProperty("layer")] public string Layer { get; set; } = "Default";
        [JsonProperty("children")] public List<string> Children { get; set; } = new();
        [JsonProperty("components")] public List<ComponentConfig> Components { get; set; } = new();
    }

    public class ComponentConfig
    {
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("props")] public Dictionary<string, object>? Props { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────
    // Generation I/O
    // ─────────────────────────────────────────────────────────────────

    public class GenerationOptions
    {
        public string ConfigPath { get; set; } = "";
        public string UnityExePath { get; set; } = "";
        public string OutputDir { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string SceneName { get; set; } = "";
        public bool Force { get; set; }
    }

    public class GenerationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string SceneGenOutputPath { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
    }

    // Written by Unity Builder to signal pass result
    public class BuilderResult
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("error")] public string Error { get; set; } = "";
        [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
        [JsonProperty("scenes")] public List<string> Scenes { get; set; } = new();
    }
}