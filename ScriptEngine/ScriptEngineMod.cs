using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(ScriptEngine.ScriptEngineMod), "ScriptEngine", "1.0.0", "Dimava")]
[assembly: MelonGame()]

namespace ScriptEngine
{
    public class ScriptEngineMod : MelonMod
    {
        static string ScriptsDir = null!;
        static string GameDir = null!;
        static string ConfigPath = null!;
        const string ConfigFileName = "ScriptEngine.cfg";
        const string StarterScriptName = "HelloWorld.cs";
        const string StarterScriptContents =
@"using HarmonyLib;
using ScriptEngine;
using UnityEngine;

[ScriptEntry]
public sealed class HelloWorld : ScriptMod
{
    float _nextLogTime;

    protected override void OnEnable()
    {
        Log(""After the script was loaded (duplicated log)."");
        DemoTarget.Ping();
    }

    protected override void OnUpdate()
    {
        if (Time.unscaledTime < _nextLogTime)
            return;

        _nextLogTime = Time.unscaledTime + 5f;
        Log(""HelloWorld.OnUpdate()"");
    }
}

public static class DemoTarget
{
    public static void Ping()
    {
        UnityEngine.Debug.Log(""DemoTarget.Ping()"");
    }
}

[HarmonyPatch(typeof(DemoTarget), nameof(DemoTarget.Ping))]
public static class DemoTargetPingPatch
{
    static void Prefix()
    {
        UnityEngine.Debug.Log(""Harmony prefix before DemoTarget.Ping()"");
    }
}";

        static readonly Dictionary<string, LoadedScript> _loaded = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<ScriptKind, IScriptLoader> _loaders = new()
        {
            [ScriptKind.Attribute] = new AttributeScriptLoader(),
        };
        static readonly object _stateLock = new();
        static readonly Regex ScriptSectionRegex = new(@"^\[scripts\.""((?:\\.|[^""])*)""\]\s*$", RegexOptions.Compiled);
        static readonly Dictionary<string, Timer> _debounce = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _bindingEditorText = new(StringComparer.OrdinalIgnoreCase);
        static Timer? _configDebounce;
        static string? _ignoredConfigContents;
        static bool _showUi;
        static object? _windowRect;
        static object? _scrollPosition;
        static FileSystemWatcher _watcher = null!;
        static FileSystemWatcher _configWatcher = null!;
        static ScriptEngineConfig _config = new();
        static ScriptCompiler _compiler = null!;

        public override void OnUpdate()
        {
            if (RuntimeGui.GetKeyDown("F8"))
                _showUi = !_showUi;
        }

        public override void OnGUI()
        {
            if (!_showUi)
                return;

            if (!RuntimeGui.IsAvailable)
                return;

            _windowRect ??= RuntimeGui.CreateRect(40f, 40f, 560f, 640f);
            _scrollPosition ??= RuntimeGui.CreateVector2(0f, 0f);
            RuntimeGui.SetDepth(0);
            _windowRect = RuntimeGui.Window(0x51C12E, _windowRect, DrawWindow, "ScriptEngine");
        }

        public override void OnInitializeMelon()
        {
            GameDir = MelonEnvironment.GameRootDirectory;
            ScriptsDir = Path.Combine(GameDir, "Scripts");
            ConfigPath = Path.Combine(ScriptsDir, ConfigFileName);
            Directory.CreateDirectory(ScriptsDir);
            ScriptLog.ResetSessionLogs(ScriptsDir);
            EnsureStarterScript();
            _compiler = new ScriptCompiler();

            lock (_stateLock)
                ReloadConfigFromDiskAndApply();

            LoggerInstance.Msg($"ScriptEngine watching: {ScriptsDir}");

            _watcher = new FileSystemWatcher(ScriptsDir, "*.cs")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;

            _configWatcher = new FileSystemWatcher(ScriptsDir, "*.cfg")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _configWatcher.Changed += OnConfigEvent;
            _configWatcher.Created += OnConfigEvent;
            _configWatcher.Deleted += OnConfigEvent;
            _configWatcher.Renamed += OnConfigRenamed;
        }

        static void EnsureStarterScript()
        {
            if (GetCurrentScripts().Count != 0)
                return;

            var starterScriptPath = Path.Combine(ScriptsDir, StarterScriptName);
            if (File.Exists(starterScriptPath))
                return;

            File.WriteAllText(starterScriptPath, StarterScriptContents);
        }

        static void OnFileEvent(object _, FileSystemEventArgs e)
        {
            lock (_stateLock)
            {
                UnloadScript(e.FullPath);

                if (_debounce.TryGetValue(e.FullPath, out var timer))
                    timer.Dispose();

                _debounce[e.FullPath] = new Timer(_ =>
                {
                    lock (_stateLock)
                    {
                        _debounce.Remove(e.FullPath);
                        HandleScriptChanged(e.FullPath);
                    }
                }, null, 300, Timeout.Infinite);
            }
        }

        static void OnFileDeleted(object _, FileSystemEventArgs e)
        {
            lock (_stateLock)
                HandleScriptDeleted(e.FullPath);
        }

        static void OnFileRenamed(object _, RenamedEventArgs e)
        {
            lock (_stateLock)
                HandleScriptRenamed(e.OldFullPath, e.FullPath);
        }

        static void OnConfigEvent(object _, FileSystemEventArgs e)
        {
            if (!IsConfigPath(e.FullPath))
                return;

            if (ShouldIgnoreConfigEvent(e.FullPath))
                return;

            DebounceConfigReload();
        }

        static void OnConfigRenamed(object _, RenamedEventArgs e)
        {
            if (!IsConfigPath(e.OldFullPath) && !IsConfigPath(e.FullPath))
                return;

            if (ShouldIgnoreConfigEvent(e.FullPath))
                return;

            DebounceConfigReload();
        }

        static bool ShouldIgnoreConfigEvent(string path)
        {
            if (string.IsNullOrEmpty(_ignoredConfigContents))
                return false;

            if (!IsConfigPath(path) || !File.Exists(path))
                return false;

            try
            {
                var currentContents = File.ReadAllText(path);
                if (string.Equals(currentContents, _ignoredConfigContents, StringComparison.Ordinal))
                    return true;

                _ignoredConfigContents = null;
                return false;
            }
            catch
            {
                return false;
            }
        }

        static void DebounceConfigReload()
        {
            _configDebounce?.Dispose();
            _configDebounce = new Timer(_ =>
            {
                lock (_stateLock)
                    ReloadConfigFromDiskAndApply();
            }, null, 300, Timeout.Infinite);
        }

        static void HandleScriptChanged(string path)
        {
            if (!File.Exists(path))
                return;

            if (!TryGetRelativeScriptPath(path, out var relativePath))
                return;

            var currentScripts = GetCurrentScripts();
            if (!currentScripts.TryGetValue(relativePath, out var script))
            {
                UnloadScript(path);
                RemoveScriptConfigEntry(relativePath);
                return;
            }

            EnsureScriptConfigEntry(relativePath, enabled: true);
            if (!ShouldLoadScript(relativePath))
            {
                UnloadScript(path);
                MelonLogger.Msg($"[ScriptEngine] Ignoring disabled script: {relativePath}");
                return;
            }

            MelonLogger.Msg($"[ScriptEngine] Reloading: {relativePath}");
            LoadScript(script);
        }

        static void HandleScriptDeleted(string path)
        {
            if (!TryGetRelativeScriptPath(path, out var relativePath))
                return;

            UnloadScript(path);
            RemoveScriptConfigEntry(relativePath);
        }

        static void HandleScriptRenamed(string oldPath, string newPath)
        {
            string? oldRelativePath = TryGetRelativeScriptPath(oldPath, out var oldRel) ? oldRel : null;
            string? newRelativePath = TryGetRelativeScriptPath(newPath, out var newRel) ? newRel : null;

            UnloadScript(oldPath);

            if (oldRelativePath != null)
                RemoveScriptConfigEntry(oldRelativePath);

            if (newRelativePath == null)
                return;

            var currentScripts = GetCurrentScripts();
            if (!currentScripts.TryGetValue(newRelativePath, out var script))
                return;

            EnsureScriptConfigEntry(newRelativePath, enabled: true, overwrite: true);
            if (!ShouldLoadScript(newRelativePath))
            {
                MelonLogger.Msg($"[ScriptEngine] Ignoring disabled script: {newRelativePath}");
                return;
            }

            MelonLogger.Msg($"[ScriptEngine] Reloading: {newRelativePath}");
            LoadScript(script);
        }

        static void ReloadConfigFromDiskAndApply()
        {
            var currentScripts = GetCurrentScripts();
            var parsedConfig = ReadConfigFromDisk();
            var normalizedConfig = NormalizeConfig(parsedConfig, currentScripts.Keys);

            _config = normalizedConfig;
            WriteConfigIfChanged(normalizedConfig);
            ApplyConfigToRuntime(normalizedConfig, currentScripts);
        }

        static void ApplyConfigToRuntime(ScriptEngineConfig config, Dictionary<string, DiscoveredScript> currentScripts)
        {
            foreach (var loadedPath in _loaded.Keys.ToList())
            {
                if (!_loaded.TryGetValue(loadedPath, out var loaded))
                    continue;

                if (!currentScripts.ContainsKey(loaded.RelativePath) || !ShouldLoadScript(loaded.RelativePath, config))
                    UnloadScript(loadedPath);
            }

            foreach (var loaded in _loaded.Values)
                ApplyScriptBindingsToLoadedScript(loaded, config);

            if (!config.ScriptsEnabled)
                return;

            foreach (var script in currentScripts.Values.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldLoadScript(script.RelativePath, config))
                    continue;

                if (_loaded.ContainsKey(script.FullPath))
                    continue;

                MelonLogger.Msg($"[ScriptEngine] Loading enabled script: {script.RelativePath}");
                LoadScript(script);
            }
        }

        static Dictionary<string, DiscoveredScript> GetCurrentScripts() =>
            ScriptDiscovery.GetCurrentScripts(ScriptsDir);

        static ScriptEngineConfig ReadConfigFromDisk()
        {
            var config = new ScriptEngineConfig();
            if (!File.Exists(ConfigPath))
                return config;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(ConfigPath);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ScriptEngine] Failed to read {ConfigFileName}: {ex.Message}");
                return config;
            }

            ConfigSection currentSection = ConfigSection.None;
            string? currentScript = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                if (line.Equals("[scripts]", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = ConfigSection.ScriptsRoot;
                    currentScript = null;
                    continue;
                }

                var scriptSectionMatch = ScriptSectionRegex.Match(line);
                if (scriptSectionMatch.Success)
                {
                    currentSection = ConfigSection.Script;
                    currentScript = UnescapeTomlString(scriptSectionMatch.Groups[1].Value);
                    continue;
                }

                var equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                    continue;

                var key = line.Substring(0, equalsIndex).Trim();
                var value = StripTomlInlineComment(line.Substring(equalsIndex + 1).Trim());

                if (currentSection == ConfigSection.ScriptsRoot && key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseBool(value, out var parsedBool))
                        continue;

                    config.ScriptsEnabled = parsedBool;
                    continue;
                }

                if (currentSection == ConfigSection.Script && currentScript != null)
                {
                    if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryParseBool(value, out var parsedBool))
                            continue;

                        config.ScriptEnabled[currentScript] = parsedBool;
                        continue;
                    }

                    if (key.Equals("error", StringComparison.OrdinalIgnoreCase) && TryParseTomlString(value, out var parsedString))
                        config.ScriptErrors[currentScript] = parsedString;

                    if (TryParseScriptConfigKey(key, out var configId))
                    {
                        var rawValue = ParseTomlValueAsRawString(value);
                        GetOrCreateScriptValues(config, currentScript)[configId] = rawValue;

                        if (TryParseTomlString(value, out var parsedBinding))
                            GetOrCreateScriptBindings(config, currentScript)[configId] = parsedBinding;
                    }
                }
            }

            return config;
        }

        static ScriptEngineConfig NormalizeConfig(ScriptEngineConfig config, IEnumerable<string> currentScripts)
        {
            var normalized = new ScriptEngineConfig
            {
                ScriptsEnabled = config.ScriptsEnabled,
            };

            foreach (var script in currentScripts.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                normalized.ScriptEnabled[script] = config.ScriptEnabled.TryGetValue(script, out var enabled) ? enabled : true;
                if (config.ScriptErrors.TryGetValue(script, out var error) && !string.IsNullOrWhiteSpace(error))
                    normalized.ScriptErrors[script] = error;
                if (config.ScriptValues.TryGetValue(script, out var values) && values.Count != 0)
                    normalized.ScriptValues[script] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                if (config.ScriptBindings.TryGetValue(script, out var bindings) && bindings.Count != 0)
                    normalized.ScriptBindings[script] = new Dictionary<string, string>(bindings, StringComparer.OrdinalIgnoreCase);
            }

            return normalized;
        }

        static void WriteConfigIfChanged(ScriptEngineConfig config)
        {
            var contents = SerializeConfig(config);
            string? currentContents = null;
            if (File.Exists(ConfigPath))
            {
                try { currentContents = File.ReadAllText(ConfigPath); }
                catch { }
            }

            if (string.Equals(currentContents, contents, StringComparison.Ordinal))
                return;

            _ignoredConfigContents = contents;
            File.WriteAllText(ConfigPath, contents);
        }

        static string SerializeConfig(ScriptEngineConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[scripts]");
            sb.Append("enabled = ");
            sb.AppendLine(config.ScriptsEnabled ? "true" : "false");

            foreach (var script in config.ScriptEnabled.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine();
                sb.Append("[scripts.\"");
                sb.Append(EscapeTomlString(script.Key));
                sb.AppendLine("\"]");
                sb.Append("enabled = ");
                sb.AppendLine(script.Value ? "true" : "false");
                if (config.ScriptErrors.TryGetValue(script.Key, out var error) && !string.IsNullOrWhiteSpace(error))
                {
                    sb.Append("error = \"");
                    sb.Append(EscapeTomlString(error));
                    sb.AppendLine("\"");
                }

                if (config.ScriptBindings.TryGetValue(script.Key, out var bindings))
                {
                    foreach (var binding in bindings.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (binding.Key.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                            || binding.Key.Equals("error", StringComparison.OrdinalIgnoreCase))
                            continue;

                        sb.Append(MakeConfigKey(binding.Key));
                        sb.Append(" = \"");
                        sb.Append(EscapeTomlString(binding.Value));
                        sb.AppendLine("\"");
                    }
                }

                if (config.ScriptValues.TryGetValue(script.Key, out var values))
                {
                    foreach (var value in values.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (value.Key.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                            || value.Key.Equals("error", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (bindings != null && bindings.ContainsKey(value.Key))
                            continue;

                        sb.Append(MakeConfigKey(value.Key));
                        sb.Append(" = ");
                        sb.AppendLine(FormatRawConfigValue(value.Value));
                    }
                }
            }

            return sb.ToString();
        }

        static void EnsureScriptConfigEntry(string relativePath, bool enabled, bool overwrite = false)
        {
            if (!overwrite && _config.ScriptEnabled.ContainsKey(relativePath))
                return;

            _config.ScriptEnabled[relativePath] = enabled;
            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
        }

        static void RemoveScriptConfigEntry(string relativePath)
        {
            if (!_config.ScriptEnabled.Remove(relativePath))
                return;

            _config.ScriptErrors.Remove(relativePath);
            _config.ScriptBindings.Remove(relativePath);
            _config.ScriptValues.Remove(relativePath);
            ClearBindingEditorState(relativePath);
            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
        }

        static void SetScriptsEnabled(bool enabled)
        {
            _config.ScriptsEnabled = enabled;
            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
            ApplyConfigToRuntime(_config, GetCurrentScripts());
        }

        static void SetScriptEnabled(string relativePath, bool enabled)
        {
            _config.ScriptEnabled[relativePath] = enabled;
            if (enabled)
                _config.ScriptErrors.Remove(relativePath);

            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
            ApplyConfigToRuntime(_config, GetCurrentScripts());
        }

        static void SetScriptError(string relativePath, string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                _config.ScriptErrors.Remove(relativePath);
            else
                _config.ScriptErrors[relativePath] = error;

            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
        }

        internal static string RegisterScriptKeyBinding(string relativePath, string bindingId, string defaultBinding)
        {
            lock (_stateLock)
            {
                var bindings = GetOrCreateScriptBindings(_config, relativePath);
                var values = GetOrCreateScriptValues(_config, relativePath);
                var normalizedDefaultBinding = NormalizeBindingText(defaultBinding);

                if (!bindings.TryGetValue(bindingId, out var configuredBinding))
                {
                    configuredBinding = values.TryGetValue(bindingId, out var configuredRawBinding)
                        ? NormalizeBindingText(configuredRawBinding)
                        : normalizedDefaultBinding;
                    bindings[bindingId] = configuredBinding;
                    values[bindingId] = configuredBinding;
                    _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
                    WriteConfigIfChanged(_config);
                }
                else
                {
                    var normalizedConfiguredBinding = NormalizeBindingText(configuredBinding);
                    if (!string.Equals(normalizedConfiguredBinding, configuredBinding, StringComparison.Ordinal))
                    {
                        configuredBinding = normalizedConfiguredBinding;
                        bindings[bindingId] = configuredBinding;
                        values[bindingId] = configuredBinding;
                        _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
                        WriteConfigIfChanged(_config);
                    }
                    else
                    {
                        configuredBinding = normalizedConfiguredBinding;
                    }
                }

                return configuredBinding;
            }
        }

        internal static string RegisterScriptConfigValue(string relativePath, string configId, string defaultValue)
        {
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(configId))
                    throw new ArgumentException("Config id cannot be null or empty.", nameof(configId));

                if (configId.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    var enabled = _config.ScriptEnabled.TryGetValue(relativePath, out var configuredEnabled)
                        ? configuredEnabled
                        : true;
                    return enabled ? "true" : "false";
                }

                var values = GetOrCreateScriptValues(_config, relativePath);
                if (!values.TryGetValue(configId, out var configuredValue))
                {
                    configuredValue = defaultValue ?? "";
                    values[configId] = configuredValue;
                    _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
                    WriteConfigIfChanged(_config);
                }

                return configuredValue;
            }
        }

        internal static void SetScriptConfigValue(string relativePath, string configId, string rawValue)
        {
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(configId))
                    throw new ArgumentException("Config id cannot be null or empty.", nameof(configId));

                if (configId.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseBool(rawValue, out var enabled))
                        SetScriptEnabled(relativePath, enabled);
                    return;
                }

                GetOrCreateScriptValues(_config, relativePath)[configId] = rawValue ?? "";
                _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
                WriteConfigIfChanged(_config);
            }
        }

        static void SetScriptBinding(string relativePath, string bindingId, string bindingText)
        {
            var bindings = GetOrCreateScriptBindings(_config, relativePath);
            bindings[bindingId] = bindingText;
            GetOrCreateScriptValues(_config, relativePath)[bindingId] = bindingText;
            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
            ApplyScriptBindingsToLoadedScript(relativePath, _config);
        }

        static IReadOnlyDictionary<string, string> GetScriptBindings(string relativePath)
        {
            if (_config.ScriptBindings.TryGetValue(relativePath, out var bindings))
                return bindings;

            return EmptyBindings.Instance;
        }

        static IEnumerable<string> GetScriptBindingIds(string relativePath)
        {
            if (_config.ScriptBindings.TryGetValue(relativePath, out var bindings))
            {
                foreach (var bindingId in bindings.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
                    yield return bindingId;
            }
        }

        static void ApplyScriptBindingsToLoadedScript(LoadedScript loadedScript, ScriptEngineConfig config)
        {
            if (loadedScript.Mod == null)
                return;

            if (!config.ScriptBindings.TryGetValue(loadedScript.RelativePath, out var bindings))
                bindings = EmptyBindings.Instance;

            loadedScript.Mod.ScriptEngineApplyKeyBindings(bindings);
        }

        static void ApplyScriptBindingsToLoadedScript(string relativePath, ScriptEngineConfig config)
        {
            foreach (var loadedScript in _loaded.Values.Where(script => string.Equals(script.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
                ApplyScriptBindingsToLoadedScript(loadedScript, config);
        }

        static void DrawWindow(int windowId)
        {
            RuntimeGui.BeginVertical();
            RuntimeGui.Label("F8 toggles this window.");

            lock (_stateLock)
            {
                bool scriptsEnabled = _config.ScriptsEnabled;
                bool nextScriptsEnabled = RuntimeGui.Toggle(scriptsEnabled, "Scripts enabled");
                if (nextScriptsEnabled != scriptsEnabled)
                    SetScriptsEnabled(nextScriptsEnabled);

                _scrollPosition = RuntimeGui.BeginScrollView(_scrollPosition!);
                foreach (var script in _config.ScriptEnabled.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    bool nextEnabled = RuntimeGui.Toggle(script.Value, script.Key);
                    if (nextEnabled != script.Value)
                        SetScriptEnabled(script.Key, nextEnabled);

                    foreach (var bindingId in GetScriptBindingIds(script.Key))
                        DrawBindingEditor(script.Key, bindingId);

                    if (_config.ScriptErrors.TryGetValue(script.Key, out var error) && !string.IsNullOrWhiteSpace(error))
                        RuntimeGui.Label($"error: {error.Replace("\r", "").Replace("\n", " | ")}");
                }

                RuntimeGui.EndScrollView();
            }

            RuntimeGui.EndVertical();
            RuntimeGui.DragWindow(0f, 0f, 10000f, 24f);
        }

        static void DrawBindingEditor(string relativePath, string bindingId)
        {
            var editorKey = MakeBindingEditorKey(relativePath, bindingId);
            var currentBindings = GetScriptBindings(relativePath);
            var currentBinding = currentBindings.TryGetValue(bindingId, out var configuredBinding)
                ? configuredBinding
                : "";
            if (!_bindingEditorText.TryGetValue(editorKey, out var pendingBinding) || string.Equals(pendingBinding, currentBinding, StringComparison.Ordinal))
                pendingBinding = currentBinding;

            RuntimeGui.BeginHorizontal();
            RuntimeGui.Label(bindingId);
            var nextPendingBinding = RuntimeGui.TextField(pendingBinding);
            if (!string.Equals(nextPendingBinding, pendingBinding, StringComparison.Ordinal))
                _bindingEditorText[editorKey] = nextPendingBinding;
            else if (!_bindingEditorText.ContainsKey(editorKey))
                _bindingEditorText[editorKey] = pendingBinding;

            pendingBinding = _bindingEditorText[editorKey];

            if (!string.Equals(pendingBinding, currentBinding, StringComparison.Ordinal))
            {
                if (!TryNormalizeBindingText(pendingBinding, out var normalizedBinding, out _))
                {
                    RuntimeGui.Label("!");
                }
                else
                {
                    if (RuntimeGui.Button("OK"))
                    {
                        SetScriptBinding(relativePath, bindingId, normalizedBinding);
                        _bindingEditorText[editorKey] = normalizedBinding;
                    }
                    if (RuntimeGui.Button("X"))
                        _bindingEditorText[editorKey] = currentBinding;
                }
            }

            RuntimeGui.EndHorizontal();
        }

        static bool ShouldLoadScript(string relativePath) => ShouldLoadScript(relativePath, _config);

        static bool ShouldLoadScript(string relativePath, ScriptEngineConfig config)
        {
            if (!config.ScriptsEnabled)
                return false;

            if (!config.ScriptEnabled.TryGetValue(relativePath, out var enabled))
                return true;

            return enabled;
        }

        static bool TryGetRelativeScriptPath(string path, out string relativePath) =>
            ScriptDiscovery.TryGetRelativeScriptPath(ScriptsDir, path, out relativePath);

        static bool IsConfigPath(string path) =>
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(ConfigPath), StringComparison.OrdinalIgnoreCase);

        static bool TryParseBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
                return true;

            result = false;
            return false;
        }

        static bool TryNormalizeBindingText(string? bindingText, out string normalizedBinding, out string error) =>
            InputRuntime.TryParseBindingText(bindingText, out _, out normalizedBinding, out error);

        static string NormalizeBindingText(string? bindingText) =>
            TryNormalizeBindingText(bindingText, out var normalizedBinding, out _)
                ? normalizedBinding
                : "";

        static Dictionary<string, string> GetOrCreateScriptBindings(ScriptEngineConfig config, string relativePath)
        {
            if (!config.ScriptBindings.TryGetValue(relativePath, out var bindings))
            {
                bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                config.ScriptBindings[relativePath] = bindings;
            }

            return bindings;
        }

        static Dictionary<string, string> GetOrCreateScriptValues(ScriptEngineConfig config, string relativePath)
        {
            if (!config.ScriptValues.TryGetValue(relativePath, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                config.ScriptValues[relativePath] = values;
            }

            return values;
        }

        static bool TryParseScriptConfigKey(string key, out string configId)
        {
            configId = "";
            if (TryParseTomlString(key, out var parsedKey))
                key = parsedKey;

            if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("error", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            configId = key;
            return true;
        }

        static string MakeConfigKey(string configId) =>
            IsSimpleTomlKey(configId)
                ? configId
                : $"\"{EscapeTomlString(configId)}\"";

        static string MakeBindingEditorKey(string relativePath, string bindingId) => $"{relativePath}\n{bindingId}";

        static void ClearBindingEditorState(string relativePath)
        {
            foreach (var key in _bindingEditorText.Keys.Where(key => key.StartsWith(relativePath + "\n", StringComparison.OrdinalIgnoreCase)).ToList())
                _bindingEditorText.Remove(key);
        }

        static bool IsSimpleTomlKey(string key) =>
            key.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');

        static string EscapeTomlString(string value) =>
            value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");

        static string UnescapeTomlString(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool escaping = false;
            foreach (var ch in value)
            {
                if (escaping)
                {
                    sb.Append(ch switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => ch,
                    });
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                sb.Append(ch);
            }

            if (escaping)
                sb.Append('\\');

            return sb.ToString();
        }

        static bool TryParseTomlString(string value, out string result)
        {
            result = "";
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return false;

            result = UnescapeTomlString(value.Substring(1, value.Length - 2));
            return true;
        }

        static string ParseTomlValueAsRawString(string value) =>
            TryParseTomlString(value, out var parsedString)
                ? parsedString
                : value.Trim();

        static string FormatRawConfigValue(string value)
        {
            if (bool.TryParse(value, out _))
                return value.ToLowerInvariant();

            if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                return value;

            if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return value;

            return $"\"{EscapeTomlString(value)}\"";
        }

        static string StripTomlInlineComment(string value)
        {
            bool inString = false;
            bool escaping = false;
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (ch == '#' && !inString)
                    return value.Substring(0, i).TrimEnd();
            }

            return value;
        }

        static void LoadScript(DiscoveredScript script)
        {
            var fullPath = Path.GetFullPath(script.FullPath);
            UnloadScript(fullPath);

            var log = ScriptLog.ForScript(ScriptsDir, script.RelativePath);
            var assembly = _compiler.Compile(script, log, out var compileError);
            if (assembly == null)
            {
                SetScriptError(script.RelativePath, compileError);
                return;
            }

            var loader = _loaders[script.Kind];
            var loaded = loader.Load(script, assembly, log, HandleScriptRuntimeException, out var loadError);
            if (loaded == null)
            {
                SetScriptError(script.RelativePath, loadError);
                return;
            }

            SetScriptError(script.RelativePath, null);
            _loaded[fullPath] = loaded;
        }

        static void UnloadScript(string path)
        {
            path = Path.GetFullPath(path);
            if (!_loaded.TryGetValue(path, out var script))
                return;

            try
            {
                script.OnUnload?.Invoke();
            }
            finally
            {
                _loaded.Remove(path);
            }
        }

        static void HandleScriptRuntimeException(string relativePath, string callbackName, Exception exception)
        {
            lock (_stateLock)
            {
                var loaded = _loaded.Values.FirstOrDefault(script => string.Equals(script.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                if (loaded == null)
                    return;

                loaded.Log.Error($"Unhandled {callbackName} exception: {exception}");
                loaded.Log.Warn("Disabling script after runtime exception.");
                SetScriptError(relativePath, exception.ToString());
                SetScriptEnabled(relativePath, false);
            }
        }
    }

    enum ConfigSection
    {
        None,
        ScriptsRoot,
        Script,
    }

    class ScriptEngineConfig
    {
        public bool ScriptsEnabled = true;
        public Dictionary<string, bool> ScriptEnabled = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ScriptErrors = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> ScriptValues = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> ScriptBindings = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class EmptyBindings : Dictionary<string, string>
    {
        public static readonly EmptyBindings Instance = new();

        EmptyBindings()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }
    }

    static class RuntimeGui
    {
        static bool _initialized;
        static bool _loggedInitializationFailure;
        static Type? _inputType;
        static Type? _keyCodeType;
        static Type? _guiType;
        static Type? _guiLayoutType;
        static Type? _guiLayoutOptionType;
        static Type? _rectType;
        static Type? _vector2Type;
        static Type? _windowFunctionType;
        static MethodInfo? _inputGetKeyDown;
        static MethodInfo? _guiWindow;
        static MethodInfo? _guiDragWindow;
        static PropertyInfo? _guiDepthProperty;
        static MethodInfo? _beginVertical;
        static MethodInfo? _endVertical;
        static MethodInfo? _beginHorizontal;
        static MethodInfo? _endHorizontal;
        static MethodInfo? _label;
        static MethodInfo? _space;
        static MethodInfo? _toggle;
        static MethodInfo? _textField;
        static MethodInfo? _button;
        static MethodInfo? _beginScrollView;
        static MethodInfo? _endScrollView;
        static Array? _emptyLayoutOptions;

        public static bool IsAvailable => EnsureInitialized();

        public static bool GetKeyDown(string keyName)
        {
            if (!EnsureInitialized())
                return false;

            object keyCode = Enum.Parse(_keyCodeType!, keyName);
            return (bool)_inputGetKeyDown!.Invoke(null, new[] { keyCode })!;
        }

        public static object CreateRect(float x, float y, float width, float height)
        {
            EnsureInitializedOrThrow();
            return Activator.CreateInstance(_rectType!, new object[] { x, y, width, height })!;
        }

        public static object CreateVector2(float x, float y)
        {
            EnsureInitializedOrThrow();
            return Activator.CreateInstance(_vector2Type!, new object[] { x, y })!;
        }

        public static void SetDepth(int depth)
        {
            if (!EnsureInitialized())
                return;

            _guiDepthProperty!.SetValue(null, depth);
        }

        public static object Window(int id, object rect, Action<int> callback, string title)
        {
            EnsureInitializedOrThrow();
            var windowDelegate = Delegate.CreateDelegate(_windowFunctionType!, callback.Target, callback.Method);
            return _guiWindow!.Invoke(null, new[] { (object)id, rect, windowDelegate, title })!;
        }

        public static void DragWindow(float x, float y, float width, float height)
        {
            if (!EnsureInitialized())
                return;

            _guiDragWindow!.Invoke(null, new[] { CreateRect(x, y, width, height) });
        }

        public static void BeginVertical()
        {
            EnsureInitializedOrThrow();
            _beginVertical!.Invoke(null, new object[] { _emptyLayoutOptions! });
        }

        public static void EndVertical()
        {
            if (!EnsureInitialized())
                return;

            _endVertical!.Invoke(null, Array.Empty<object>());
        }

        public static void BeginHorizontal()
        {
            EnsureInitializedOrThrow();
            _beginHorizontal!.Invoke(null, new object[] { _emptyLayoutOptions! });
        }

        public static void EndHorizontal()
        {
            if (!EnsureInitialized())
                return;

            _endHorizontal!.Invoke(null, Array.Empty<object>());
        }

        public static void Label(string text)
        {
            EnsureInitializedOrThrow();
            _label!.Invoke(null, new object[] { text, _emptyLayoutOptions! });
        }

        public static void Space(float pixels)
        {
            EnsureInitializedOrThrow();
            _space!.Invoke(null, new object[] { pixels });
        }

        public static bool Toggle(bool value, string text)
        {
            EnsureInitializedOrThrow();
            return (bool)_toggle!.Invoke(null, new object[] { value, text, _emptyLayoutOptions! })!;
        }

        public static string TextField(string text)
        {
            EnsureInitializedOrThrow();
            return (string)_textField!.Invoke(null, new object[] { text, _emptyLayoutOptions! })!;
        }

        public static bool Button(string text)
        {
            EnsureInitializedOrThrow();
            return (bool)_button!.Invoke(null, new object[] { text, _emptyLayoutOptions! })!;
        }

        public static object BeginScrollView(object scrollPosition)
        {
            EnsureInitializedOrThrow();
            return _beginScrollView!.Invoke(null, new[] { scrollPosition, _emptyLayoutOptions! })!;
        }

        public static void EndScrollView()
        {
            if (!EnsureInitialized())
                return;

            _endScrollView!.Invoke(null, Array.Empty<object>());
        }

        static void EnsureInitializedOrThrow()
        {
            if (!EnsureInitialized())
                throw new InvalidOperationException("Unity IMGUI runtime types are unavailable.");
        }

        static bool EnsureInitialized()
        {
            if (_initialized)
                return true;

            try
            {
                _inputType = FindType("UnityEngine.Input");
                _keyCodeType = FindType("UnityEngine.KeyCode");
                _guiType = FindType("UnityEngine.GUI");
                _guiLayoutType = FindType("UnityEngine.GUILayout");
                _guiLayoutOptionType = FindType("UnityEngine.GUILayoutOption");
                _rectType = FindType("UnityEngine.Rect");
                _vector2Type = FindType("UnityEngine.Vector2");

                if (_inputType == null || _keyCodeType == null || _guiType == null || _guiLayoutType == null || _guiLayoutOptionType == null || _rectType == null || _vector2Type == null)
                    return false;

                _inputGetKeyDown = _inputType.GetMethod("GetKeyDown", BindingFlags.Public | BindingFlags.Static, null, new[] { _keyCodeType }, null);
                _guiDepthProperty = _guiType.GetProperty("depth", BindingFlags.Public | BindingFlags.Static);
                _guiWindow = _guiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Window"
                        && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType == typeof(int)
                        && m.GetParameters()[1].ParameterType == _rectType
                        && typeof(Delegate).IsAssignableFrom(m.GetParameters()[2].ParameterType)
                        && m.GetParameters()[3].ParameterType == typeof(string));
                _guiDragWindow = _guiType.GetMethod("DragWindow", BindingFlags.Public | BindingFlags.Static, null, new[] { _rectType }, null);
                _beginVertical = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "BeginVertical"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.IsArray);
                _endVertical = _guiLayoutType.GetMethod("EndVertical", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _beginHorizontal = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "BeginHorizontal"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.IsArray);
                _endHorizontal = _guiLayoutType.GetMethod("EndHorizontal", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                _label = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Label"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType.IsArray);
                _space = _guiLayoutType.GetMethod("Space", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float) }, null);
                _toggle = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Toggle"
                        && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(bool)
                        && m.GetParameters()[1].ParameterType == typeof(string)
                        && m.GetParameters()[2].ParameterType.IsArray);
                _textField = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "TextField"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType.IsArray);
                _button = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Button"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType.IsArray);
                _beginScrollView = _guiLayoutType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "BeginScrollView"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == _vector2Type
                        && m.GetParameters()[1].ParameterType.IsArray);
                _endScrollView = _guiLayoutType.GetMethod("EndScrollView", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                if (_inputGetKeyDown == null || _guiDepthProperty == null || _guiWindow == null || _guiDragWindow == null || _beginVertical == null || _endVertical == null || _beginHorizontal == null || _endHorizontal == null || _label == null || _space == null || _toggle == null || _textField == null || _button == null || _beginScrollView == null || _endScrollView == null)
                    return false;

                _windowFunctionType = _guiWindow.GetParameters()[2].ParameterType;
                _emptyLayoutOptions = Array.CreateInstance(_guiLayoutOptionType, 0);
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                if (!_loggedInitializationFailure)
                {
                    MelonLogger.Error($"[ScriptEngine] Failed to initialize IMGUI bridge: {ex.Message}");
                    _loggedInitializationFailure = true;
                }

                return false;
            }
        }

        static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch { }
            }

            return null;
        }
    }
}
