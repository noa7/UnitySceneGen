using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySceneGen.Core
{
    // ─────────────────────────────────────────────────────────────────
    // Config models
    // ─────────────────────────────────────────────────────────────────

    public class SceneGenConfig
    {
        [JsonProperty("project")]     public ProjectConfig?          Project     { get; set; }
        [JsonProperty("settings")]    public SettingsConfig?         Settings    { get; set; }
        [JsonProperty("scripts")]     public List<ScriptConfig>      Scripts     { get; set; } = new();
        [JsonProperty("scenes")]      public List<SceneConfig>       Scenes      { get; set; } = new();
        [JsonProperty("gameObjects")] public List<GameObjectConfig>  GameObjects { get; set; } = new();
    }

    public class ScriptConfig
    {
        /// <summary>Class name and .cs filename without extension, e.g. "PlayerController".</summary>
        [JsonProperty("name")] public string Name { get; set; } = "";

        /// <summary>
        /// Path to an external .cs file relative to the manifest or config file.
        /// When set, the file is copied directly into Assets/Scripts/ with no wrapping.
        ///
        /// Use in manifest format:       listed under "scripts" in the manifest
        /// Use in single-config format:  { "name": "Foo", "file": "scripts/Foo.cs" }
        ///
        /// When File is set, Body and Namespace are ignored.
        /// </summary>
        [JsonProperty("file")] public string File { get; set; } = "";

        /// <summary>
        /// Optional namespace wrapper. Only used when generating from Body (not when File is set).
        /// </summary>
        [JsonProperty("namespace")] public string Namespace { get; set; } = "";

        /// <summary>
        /// C# source for the class body. Ignored when File is set.
        ///
        /// Preferred format — array of lines, no escaping needed:
        ///   ["    void Start() { }", "", "    void Update() { }"]
        ///
        /// Legacy format — plain string with \n:
        ///   "    void Start() { }\n\n    void Update() { }"
        ///
        /// UnitySceneGen wraps the body in a MonoBehaviour class automatically.
        /// Leave empty for a stub with Start() and Update().
        /// </summary>
        [JsonProperty("body")]
        [JsonConverter(typeof(StringOrLinesConverter))]
        public string Body { get; set; } = "";
    }

    public class ProjectConfig
    {
        [JsonProperty("name")]         public string       Name         { get; set; } = "MyProject";
        [JsonProperty("unityVersion")] public string       UnityVersion { get; set; } = "2022.3.20f1";
        [JsonProperty("packages")]     public List<string> Packages     { get; set; } = new();
    }

    public class SettingsConfig
    {
        [JsonProperty("tags")]   public List<string> Tags   { get; set; } = new();
        [JsonProperty("layers")] public List<string> Layers { get; set; } = new();
    }

    public class SceneConfig
    {
        [JsonProperty("name")]  public string       Name  { get; set; } = "Main";
        [JsonProperty("path")]  public string       Path  { get; set; } = "Assets/Scenes/Main.unity";
        [JsonProperty("roots")] public List<string> Roots { get; set; } = new();
    }

    public class GameObjectConfig
    {
        [JsonProperty("id")]         public string                    Id         { get; set; } = "";
        [JsonProperty("name")]       public string                    Name       { get; set; } = "GameObject";
        [JsonProperty("active")]     public bool                      Active     { get; set; } = true;
        [JsonProperty("tag")]        public string                    Tag        { get; set; } = "Untagged";
        [JsonProperty("layer")]      public string                    Layer      { get; set; } = "Default";
        [JsonProperty("children")]   public List<string>              Children   { get; set; } = new();
        [JsonProperty("components")] public List<ComponentConfig>     Components { get; set; } = new();

        /// <summary>
        /// Optional built-in or file-based template to expand into this GameObject's
        /// component list before validation and scene building.
        ///
        /// Built-in templates (always available, no files needed):
        ///   "camera/main"        — Camera + AudioListener
        ///   "light/directional"  — Directional Light
        ///   "ui/canvas"          — Canvas + CanvasScaler + GraphicRaycaster
        ///   "ui/eventsystem"     — EventSystem + StandaloneInputModule
        ///   "ui/panel"           — RectTransform + Image (fills parent)
        ///   "ui/button"          — RectTransform + Image + Button + TextMeshProUGUI
        ///   "ui/label"           — RectTransform + TextMeshProUGUI
        ///   "physics/rigidbody"  — Rigidbody + BoxCollider
        ///   "physics/trigger"    — BoxCollider (isTrigger = true)
        ///   "audio/source"       — AudioSource
        ///
        /// File-based templates: place a .json file in the templates/ folder next to
        /// your manifest and reference it by name (without .json extension).
        ///
        /// Template components are the base; explicit "components" on this GameObject
        /// are merged on top and always win over template defaults.
        ///
        /// This field is cleared after resolution — the validator and builder only
        /// ever see the fully expanded component list.
        /// </summary>
        [JsonProperty("template")] public string? Template { get; set; }

        /// <summary>
        /// Friendly prop overrides for the template. Keys are the template's declared
        /// prop-mapping names, not raw Unity prop names.
        ///
        /// Examples for "ui/button":
        ///   { "label": "Start Game", "labelColor": "#FFFFFFFF", "bgColor": "#1A73E8FF" }
        ///
        /// See GET /schema → templates for each template's available prop names.
        /// </summary>
        [JsonProperty("templateProps")] public Dictionary<string, object>? TemplateProps { get; set; }
    }

    public class ComponentConfig
    {
        [JsonProperty("type")]  public string                     Type  { get; set; } = "";
        [JsonProperty("props")] public Dictionary<string, object>? Props { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────
    // Generation I/O
    // ─────────────────────────────────────────────────────────────────

    public class GenerationOptions
    {
        public string ConfigPath   { get; set; } = "";
        public string UnityExePath { get; set; } = "";
        public string OutputDir    { get; set; } = "";
        public string ProjectName  { get; set; } = "";
        public string SceneName    { get; set; } = "";
        public bool   Force        { get; set; }
    }

    public class GenerationResult
    {
        public bool         Success            { get; set; }
        public string       Error              { get; set; } = "";
        public string       ProjectPath        { get; set; } = "";
        public string       SceneGenOutputPath { get; set; } = "";
        public List<string> Warnings           { get; set; } = new();
    }

    // Written by Unity Builder to signal pass result
    public class BuilderResult
    {
        [JsonProperty("success")]  public bool         Success  { get; set; }
        [JsonProperty("error")]    public string       Error    { get; set; } = "";
        [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
        [JsonProperty("scenes")]   public List<string> Scenes   { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────
    // StringOrLinesConverter
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Allows "body" to be a plain string or an array of strings.
    /// Array form requires no escaping — each element is one line of C# source.
    /// </summary>
    public sealed class StringOrLinesConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(
            JsonReader reader, Type objectType,
            object? existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.String: return reader.Value?.ToString() ?? "";
                case JsonToken.Null:   return "";
                case JsonToken.StartArray:
                {
                    var lines = new List<string>();
                    while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                        lines.Add(reader.Value?.ToString() ?? "");
                    return string.Join("\n", lines);
                }
                default: return JToken.Load(reader).ToString();
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            => writer.WriteValue(value?.ToString() ?? "");

        public override bool CanWrite => true;
    }
}
