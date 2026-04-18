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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
using MelonLoader;
using UnityEngine;

public static class HelloWorld
{
    static readonly Harmony Harmony = new(""scriptengine.helloworld"");
    static GameObject? _gameObject;

    public static void OnLoad()
    {
        MelonLogger.Msg(""Hello from ScriptEngine!"");

        if (_gameObject != null)
            GameObject.Destroy(_gameObject);

        _gameObject = new GameObject(""__ScriptEngineHelloWorld__"");
        GameObject.DontDestroyOnLoad(_gameObject);
        _gameObject.AddComponent<HelloWorldBehaviour>();

        Harmony.UnpatchSelf();
        Harmony.PatchAll(typeof(HelloWorld).Assembly);
        DemoTarget.Ping();
    }

    public static void OnUnload()
    {
        Harmony.UnpatchSelf();

        if (_gameObject != null)
        {
            GameObject.Destroy(_gameObject);
            _gameObject = null;
        }
    }
}

public sealed class HelloWorldBehaviour : MonoBehaviour
{
    float _nextLogTime;

    void Update()
    {
        if (Time.unscaledTime < _nextLogTime)
            return;

        _nextLogTime = Time.unscaledTime + 5f;
        MelonLogger.Msg(""HelloWorldBehaviour.Update()"");
    }
}

public static class DemoTarget
{
    public static void Ping()
    {
        MelonLogger.Msg(""DemoTarget.Ping()"");
    }
}

[HarmonyPatch(typeof(DemoTarget), nameof(DemoTarget.Ping))]
public static class DemoTargetPingPatch
{
    static void Prefix()
    {
        MelonLogger.Msg(""Harmony prefix before DemoTarget.Ping()"");
    }
}";

        // track loaded scripts: file path -> (assembly, onUnload action)
        static readonly Dictionary<string, LoadedScript> _loaded = new(StringComparer.OrdinalIgnoreCase);
        static readonly object _stateLock = new();
        static readonly Regex ScriptSectionRegex = new(@"^\[scripts\.""((?:\\.|[^""])*)""\]\s*$", RegexOptions.Compiled);
        static readonly Dictionary<string, Timer> _debounce = new(StringComparer.OrdinalIgnoreCase);
        static Timer? _configDebounce;
        static FileSystemWatcher _watcher = null!;
        static FileSystemWatcher _configWatcher = null!;
        static ScriptEngineConfig _config = new();

        public override void OnInitializeMelon()
        {
            GameDir = MelonEnvironment.GameRootDirectory;
            ScriptsDir = Path.Combine(GameDir, "Scripts");
            ConfigPath = Path.Combine(ScriptsDir, ConfigFileName);
            Directory.CreateDirectory(ScriptsDir);
            EnsureStarterScript();
            lock (_stateLock)
                ReloadConfigFromDiskAndApply();

            LoggerInstance.Msg($"ScriptEngine watching: {ScriptsDir}");

            // Watch for changes
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
            if (Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories).Length != 0)
                return;

            var starterScriptPath = Path.Combine(ScriptsDir, StarterScriptName);
            if (File.Exists(starterScriptPath))
                return;

            File.WriteAllText(starterScriptPath, StarterScriptContents);
        }

        // Debounce: FSW fires multiple events per save
        static void OnFileEvent(object _, FileSystemEventArgs e)
        {
            if (_debounce.TryGetValue(e.FullPath, out var t)) t.Dispose();
            _debounce[e.FullPath] = new Timer(_ =>
            {
                _debounce.Remove(e.FullPath);
                lock (_stateLock)
                    HandleScriptChanged(e.FullPath);
            }, null, 300, Timeout.Infinite);
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

            DebounceConfigReload();
        }

        static void OnConfigRenamed(object _, RenamedEventArgs e)
        {
            if (!IsConfigPath(e.OldFullPath) && !IsConfigPath(e.FullPath))
                return;

            DebounceConfigReload();
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

            EnsureScriptConfigEntry(relativePath, enabled: true);
            if (!ShouldLoadScript(relativePath))
            {
                UnloadScript(path);
                MelonLogger.Msg($"[ScriptEngine] Ignoring disabled script: {relativePath}");
                return;
            }

            MelonLogger.Msg($"[ScriptEngine] Reloading: {relativePath}");
            LoadScript(path);
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

            EnsureScriptConfigEntry(newRelativePath, enabled: true, overwrite: true);
            if (!ShouldLoadScript(newRelativePath))
            {
                MelonLogger.Msg($"[ScriptEngine] Ignoring disabled script: {newRelativePath}");
                return;
            }

            MelonLogger.Msg($"[ScriptEngine] Reloading: {newRelativePath}");
            LoadScript(newPath);
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

        static void ApplyConfigToRuntime(ScriptEngineConfig config, Dictionary<string, string> currentScripts)
        {
            foreach (var loadedPath in _loaded.Keys.ToList())
            {
                if (!TryGetRelativeScriptPath(loadedPath, out var relativePath))
                {
                    UnloadScript(loadedPath);
                    continue;
                }

                if (!currentScripts.ContainsKey(relativePath) || !ShouldLoadScript(relativePath, config))
                    UnloadScript(loadedPath);
            }

            if (config.StartWithAllScriptsDisabled)
                return;

            foreach (var script in currentScripts.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldLoadScript(script.Key, config))
                    continue;

                if (_loaded.ContainsKey(script.Value))
                    continue;

                MelonLogger.Msg($"[ScriptEngine] Loading enabled script: {script.Key}");
                LoadScript(script.Value);
            }
        }

        static Dictionary<string, string> GetCurrentScripts()
        {
            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.AllDirectories))
            {
                if (!TryGetRelativeScriptPath(file, out var relativePath))
                    continue;

                scripts[relativePath] = Path.GetFullPath(file);
            }

            return scripts;
        }

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

                if (line.Equals("[engine]", StringComparison.OrdinalIgnoreCase))
                {
                    currentSection = ConfigSection.Engine;
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
                var value = line.Substring(equalsIndex + 1).Trim();
                var commentIndex = value.IndexOf('#');
                if (commentIndex >= 0)
                    value = value.Substring(0, commentIndex).Trim();

                if (!TryParseBool(value, out var parsedBool))
                    continue;

                if (currentSection == ConfigSection.Engine && key.Equals("start_with_all_scripts_disabled", StringComparison.OrdinalIgnoreCase))
                {
                    config.StartWithAllScriptsDisabled = parsedBool;
                    continue;
                }

                if (currentSection == ConfigSection.Script && currentScript != null && key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                    config.ScriptEnabled[currentScript] = parsedBool;
            }

            return config;
        }

        static ScriptEngineConfig NormalizeConfig(ScriptEngineConfig config, IEnumerable<string> currentScripts)
        {
            var normalized = new ScriptEngineConfig
            {
                StartWithAllScriptsDisabled = config.StartWithAllScriptsDisabled,
            };

            foreach (var script in currentScripts.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                normalized.ScriptEnabled[script] = config.ScriptEnabled.TryGetValue(script, out var enabled) ? enabled : true;
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

            File.WriteAllText(ConfigPath, contents);
        }

        static string SerializeConfig(ScriptEngineConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[engine]");
            sb.Append("start_with_all_scripts_disabled = ");
            sb.AppendLine(config.StartWithAllScriptsDisabled ? "true" : "false");

            foreach (var script in config.ScriptEnabled.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine();
                sb.Append("[scripts.\"");
                sb.Append(EscapeTomlString(script.Key));
                sb.AppendLine("\"]");
                sb.Append("enabled = ");
                sb.AppendLine(script.Value ? "true" : "false");
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

            _config = NormalizeConfig(_config, GetCurrentScripts().Keys);
            WriteConfigIfChanged(_config);
        }

        static bool ShouldLoadScript(string relativePath) => ShouldLoadScript(relativePath, _config);

        static bool ShouldLoadScript(string relativePath, ScriptEngineConfig config)
        {
            if (config.StartWithAllScriptsDisabled)
                return false;

            if (!config.ScriptEnabled.TryGetValue(relativePath, out var enabled))
                return true;

            return enabled;
        }

        static bool TryGetRelativeScriptPath(string path, out string relativePath)
        {
            relativePath = "";

            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(ScriptsDir, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            relativePath = relative.Replace('\\', '/');
            return true;
        }

        static bool IsConfigPath(string path) =>
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(ConfigPath), StringComparison.OrdinalIgnoreCase);

        static bool TryParseBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
                return true;

            result = false;
            return false;
        }

        static string EscapeTomlString(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string UnescapeTomlString(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool escaping = false;
            foreach (var ch in value)
            {
                if (escaping)
                {
                    sb.Append(ch);
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

        static void LoadScript(string path)
        {
            path = Path.GetFullPath(path);

            // Unload previous version first
            UnloadScript(path);

            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex) { MelonLogger.Error($"[ScriptEngine] Read failed {Path.GetFileName(path)}: {ex.Message}"); return; }

            var assembly = Compile(path, source);
            if (assembly == null) return;

            // Find and call OnLoad() on any public static class that has it
            Action? onUnload = null;
            foreach (var type in assembly.GetTypes())
            {
                var onLoad = type.GetMethod("OnLoad", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (onLoad != null)
                {
                    try { onLoad.Invoke(null, null); }
                    catch (Exception ex) { MelonLogger.Error($"[ScriptEngine] OnLoad error in {type.Name}: {ex}"); }
                }

                var onUnloadMethod = type.GetMethod("OnUnload", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (onUnloadMethod != null)
                    onUnload += () => { try { onUnloadMethod.Invoke(null, null); } catch { } };
            }

            _loaded[path] = new LoadedScript(assembly, onUnload);
            MelonLogger.Msg($"[ScriptEngine] Loaded: {Path.GetFileName(path)}");
        }

        static void UnloadScript(string path)
        {
            path = Path.GetFullPath(path);
            if (!_loaded.TryGetValue(path, out var script)) return;
            script.OnUnload?.Invoke();
            _loaded.Remove(path);
            MelonLogger.Msg($"[ScriptEngine] Unloaded: {Path.GetFileName(path)}");
        }

        static Assembly? Compile(string path, string source)
        {
            // Build reference list from all loaded assemblies + game Managed folder
            var refs = new List<MetadataReference>();

            // All currently loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.IsNullOrEmpty(asm.Location))
                        refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch { }
            }

            // Also add everything in Managed/ that isn't already loaded
            var managedDir = MelonEnvironment.UnityGameManagedDirectory;
            if (Directory.Exists(managedDir))
            {
                var loadedPaths = new HashSet<string>(
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.Location)
                        .Where(l => !string.IsNullOrEmpty(l))
                        .Select(Path.GetFullPath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var dll in Directory.GetFiles(managedDir, "*.dll"))
                {
                    if (!loadedPaths.Contains(Path.GetFullPath(dll)))
                        try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { }
                }
            }

            var tree = CSharpSyntaxTree.ParseText(source, path: path);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true);

            var compilation = CSharpCompilation.Create(
                assemblyName: Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.Ticks,
                syntaxTrees: new[] { tree },
                references: refs,
                options: options);

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            var logPath = Path.ChangeExtension(path, ".log");

            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.GetMessage()} ({d.Location.GetLineSpan().StartLinePosition})");
                var errorText = string.Join("\n", errors);
                MelonLogger.Error($"[ScriptEngine] Compile errors in {Path.GetFileName(path)}:\n  {errorText.Replace("\n", "\n  ")}");
                File.WriteAllText(logPath, $"Compile errors in {Path.GetFileName(path)}:\n{errorText}\n");
                return null;
            }

            // Clear stale log on success
            if (File.Exists(logPath)) File.Delete(logPath);

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }

    enum ConfigSection
    {
        None,
        Engine,
        Script,
    }

    class ScriptEngineConfig
    {
        public bool StartWithAllScriptsDisabled;
        public Dictionary<string, bool> ScriptEnabled = new(StringComparer.OrdinalIgnoreCase);
    }

    class LoadedScript
    {
        public Assembly Assembly;
        public Action? OnUnload;
        public LoadedScript(Assembly assembly, Action? onUnload) { Assembly = assembly; OnUnload = onUnload; }
    }
}
