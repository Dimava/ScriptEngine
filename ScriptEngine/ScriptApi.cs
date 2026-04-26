using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;

namespace ScriptEngine
{
    public sealed class ScriptInitContext
    {
        public string ScriptId = "";
        public ScriptLog Log = null!;
        public object GameObject = null!;
        public Action<string, Exception> RuntimeExceptionHandler = null!;
        public Func<string, string, string> BindingRegistrar = null!;
        public Func<string, string, string> ConfigRegistrar = null!;
        public Action<string, string> ConfigSetter = null!;
    }

    public delegate bool TryParseConfigValue<T>(string text, out T value);

    public interface IScriptConfigEntry
    {
        string Id { get; }
        string RawValue { get; }
        void ScriptEngineApplyRawValue(string rawValue);
    }

    public sealed class ScriptConfigEntry<T>
        : IScriptConfigEntry
    {
        readonly TryParseConfigValue<T> _tryParse;
        readonly Func<T, string> _format;
        readonly Action<ScriptConfigEntry<T>> _onChanged;
        string _rawValue;

        public ScriptConfigEntry(
            string id,
            string rawValue,
            T defaultValue,
            TryParseConfigValue<T> tryParse,
            Func<T, string> format,
            Action<ScriptConfigEntry<T>> onChanged)
        {
            Id = id;
            _rawValue = rawValue;
            DefaultValue = defaultValue;
            _tryParse = tryParse;
            _format = format;
            _onChanged = onChanged;
        }

        public string Id { get; }
        public T DefaultValue { get; }

        public string RawValue
        {
            get => _rawValue;
            set
            {
                if (string.Equals(_rawValue, value, StringComparison.Ordinal))
                    return;

                _rawValue = value;
                _onChanged(this);
            }
        }

        public T Value
        {
            get
            {
                if (_tryParse(_rawValue, out var value))
                    return value;

                Value = DefaultValue;
                return DefaultValue;
            }
            set => RawValue = _format(value);
        }

        public void ScriptEngineApplyRawValue(string rawValue)
        {
            _rawValue = rawValue;
        }
    }

    public abstract class ScriptModBase
    {
        [ThreadStatic]
        public static ScriptInitContext? PendingInit;

        ScriptLog _log;
        protected object _gameObject;
        Action<string, Exception> _runtimeExceptionHandler;
        Func<string, string, string> _configRegistrar;
        Action<string, string> _configSetter;
        ScriptKeyBindings _keys;
        readonly Dictionary<string, IScriptConfigEntry> _configEntries = new(StringComparer.OrdinalIgnoreCase);

        public string ScriptId { get; }
        public ScriptKeyBindings Keys => _keys;

        protected ScriptModBase()
        {
            var ctx = PendingInit ?? throw new InvalidOperationException("ScriptModBase must be instantiated by the ScriptEngine.");
            ScriptId = ctx.ScriptId;
            _log = ctx.Log;
            _gameObject = ctx.GameObject;
            _runtimeExceptionHandler = ctx.RuntimeExceptionHandler;
            _configRegistrar = ctx.ConfigRegistrar;
            _configSetter = ctx.ConfigSetter;
            _keys = new ScriptKeyBindings(ctx.BindingRegistrar);
        }

        public void ScriptEngineClearHostObject()
        {
            _gameObject = null!;
        }

        public void ScriptEngineApplyKeyBindings(IReadOnlyDictionary<string, string> bindings)
        {
            _keys.ScriptEngineApplyBindings(bindings);
        }

        public void ScriptEngineApplyConfigValues(IReadOnlyDictionary<string, string> values)
        {
            foreach (var entry in _configEntries.Values)
            {
                if (values.TryGetValue(entry.Id, out var rawValue))
                    entry.ScriptEngineApplyRawValue(rawValue);
            }
        }

        public void Log(string message) => _log.Info(message);
        public void Warn(string message) => _log.Warn(message);
        public void Error(string message) => _log.Error(message);

        public void BindKey(string id, string defaultBinding) => _keys.Register(id, defaultBinding);
        public bool WasPressed(string id) => _keys.WasPressed(id);

        public ScriptConfigEntry<int> BindInt(string id, int defaultValue)
        {
            var defaultText = defaultValue.ToString(CultureInfo.InvariantCulture);
            var rawValue = _configRegistrar(id, defaultText);
            return RegisterConfigEntry(new ScriptConfigEntry<int>(
                id,
                rawValue,
                defaultValue,
                static (string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
                static value => value.ToString(CultureInfo.InvariantCulture),
                entry => _configSetter(entry.Id, entry.RawValue)));
        }

        public ScriptConfigEntry<float> BindFloat(string id, float defaultValue)
        {
            var defaultText = defaultValue.ToString(CultureInfo.InvariantCulture);
            var rawValue = _configRegistrar(id, defaultText);
            return RegisterConfigEntry(new ScriptConfigEntry<float>(
                id,
                rawValue,
                defaultValue,
                static (string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value),
                static value => value.ToString(CultureInfo.InvariantCulture),
                entry => _configSetter(entry.Id, entry.RawValue)));
        }

        public ScriptConfigEntry<bool> BindBool(string id, bool defaultValue)
        {
            var defaultText = defaultValue ? "true" : "false";
            var rawValue = _configRegistrar(id, defaultText);
            return RegisterConfigEntry(new ScriptConfigEntry<bool>(
                id,
                rawValue,
                defaultValue,
                static (string text, out bool value) => bool.TryParse(text, out value),
                static value => value ? "true" : "false",
                entry => _configSetter(entry.Id, entry.RawValue)));
        }

        ScriptConfigEntry<T> RegisterConfigEntry<T>(ScriptConfigEntry<T> entry)
        {
            _configEntries[entry.Id] = entry;
            return entry;
        }

        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        protected virtual void OnConfigChanged() { }
        protected virtual void OnUpdate() { }
        protected virtual void OnFixedUpdate() { }
        protected virtual void OnLateUpdate() { }
        protected virtual void OnGUI() { }

        public void ScriptEngineInvokeEnable() => OnEnable();
        public void ScriptEngineInvokeDisable() => OnDisable();
        public void ScriptEngineInvokeConfigChanged() => InvokeFrameCallback(nameof(OnConfigChanged), OnConfigChanged);
        public void ScriptEngineInvokeUpdate() => InvokeFrameCallback(nameof(OnUpdate), OnUpdate);
        public void ScriptEngineInvokeFixedUpdate() => InvokeFrameCallback(nameof(OnFixedUpdate), OnFixedUpdate);
        public void ScriptEngineInvokeLateUpdate() => InvokeFrameCallback(nameof(OnLateUpdate), OnLateUpdate);
        public void ScriptEngineInvokeGUI() => InvokeFrameCallback(nameof(OnGUI), OnGUI);

        void InvokeFrameCallback(string callbackName, Action callback)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _runtimeExceptionHandler(callbackName, ex);
            }
        }
    }

    public sealed class ScriptKeyBindings
    {
        readonly Dictionary<string, ScriptKeyBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
        readonly Func<string, string, string> _bindingRegistrar;

        public ScriptKeyBindings(Func<string, string, string> bindingRegistrar)
        {
            _bindingRegistrar = bindingRegistrar;
        }

        public void ScriptEngineApplyBindings(IReadOnlyDictionary<string, string> bindings)
        {
            foreach (var binding in _bindings.Values)
            {
                var activeBinding = bindings.GetValueOrDefault(binding.Id, binding.DefaultBindingText);
                binding.ScriptEngineSetBinding(activeBinding, binding.DefaultBindingText);
            }
        }

        public IReadOnlyCollection<ScriptKeyBinding> ScriptEngineGetBindings() => _bindings.Values;

        public void Register(string id, string defaultBinding)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Binding id cannot be null or empty.", nameof(id));

            defaultBinding ??= "";
            var activeBinding = _bindingRegistrar(id, defaultBinding);

            if (!_bindings.TryGetValue(id, out var binding))
            {
                binding = new ScriptKeyBinding(id);
                _bindings.Add(id, binding);
            }

            binding.ScriptEngineSetBinding(activeBinding, defaultBinding);
        }

        public bool WasPressed(string id)
        {
            if (_bindings.TryGetValue(id, out var binding))
                return binding.WasPressedThisFrame;
            throw new KeyNotFoundException($"Script key binding '{id}' has not been registered.");
        }
    }

    public sealed class ScriptKeyBinding
    {
        ScriptKeyChord _chord;

        public ScriptKeyBinding(string id)
        {
            Id = id;
            DefaultBindingText = "";
            _chord = ScriptKeyChord.Unbound;
        }

        public string Id { get; }
        public string DefaultBindingText { get; private set; }

        public bool WasPressedThisFrame => _chord.WasPressedThisFrame();

        public void ScriptEngineSetBinding(string bindingText, string defaultBindingText)
        {
            DefaultBindingText = defaultBindingText ?? "";
            if (!InputRuntime.TryParseBindingText(bindingText, out var chord, out _, out _))
                chord = ScriptKeyChord.Unbound;

            _chord = chord;
        }
    }

    public sealed class ScriptLog
    {
        static readonly object WriteLock = new();
        static string? _globalErrorLogPath;

        readonly string _relativePath;
        readonly string _logPath;

        public string LogPath => _logPath;

        public ScriptLog(string relativePath, string logPath)
        {
            _relativePath = relativePath;
            _logPath = logPath;
        }

        public static void ResetSessionLogs(string scriptsDir)
        {
            var logsDir = Path.Combine(scriptsDir, "logs");
            try
            {
                if (Directory.Exists(logsDir))
                    Directory.Delete(logsDir, recursive: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ScriptEngine] Failed to reset session logs: {ex.Message}");
            }

            _globalErrorLogPath = Path.Combine(logsDir, "errors.log");
        }

        public static ScriptLog ForScript(string scriptsDir, string relativePath)
        {
            var logRelativePath = Path.ChangeExtension(relativePath.Replace('/', Path.DirectorySeparatorChar), ".log");
            var logPath = Path.Combine(scriptsDir, "logs", logRelativePath);
            return new ScriptLog(relativePath, logPath);
        }

        public void Info(string message) => Write("Info", message, MelonLogger.Msg, global: false);
        public void Warn(string message) => Write("Warn", message, MelonLogger.Warning, global: false);
        public void Error(string message) => Write("Error", message, MelonLogger.Error, global: true);

        void Write(string level, string message, Action<string> melonWrite, bool global)
        {
            var formatted = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            melonWrite($"[{_relativePath}] {message}");

            lock (WriteLock)
            {
                AppendToFile(_logPath, formatted);
                if (global && _globalErrorLogPath != null)
                    AppendToFile(_globalErrorLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{_relativePath}] {message}");
            }
        }

        static void AppendToFile(string path, string line)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ScriptEngine] Failed to write log: {ex.Message}");
            }
        }
    }
}
