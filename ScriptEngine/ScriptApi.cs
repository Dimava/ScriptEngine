using System;
using System.IO;
using MelonLoader;

namespace ScriptEngine
{
    public abstract class ScriptModBase
    {
        ScriptLog? _log;
        object? _gameObject;
        Action<string, Exception>? _runtimeExceptionHandler;

        public string ScriptPath { get; private set; } = "";
        public string RelativePath { get; private set; } = "";

        protected object gameObjectObject =>
            _gameObject ?? throw new InvalidOperationException("Script host GameObject is not available.");

        internal void ScriptEngineInitialize(string scriptPath, string relativePath, ScriptLog log, object gameObject, Action<string, Exception> runtimeExceptionHandler)
        {
            ScriptPath = scriptPath;
            RelativePath = relativePath;
            _log = log;
            _gameObject = gameObject;
            _runtimeExceptionHandler = runtimeExceptionHandler;
        }

        internal void ScriptEngineClearHostObject()
        {
            _gameObject = null;
        }

        public void Log(string message)
        {
            if (_log != null)
                _log.Info(message);
        }

        public void Warn(string message)
        {
            if (_log != null)
                _log.Warn(message);
        }

        public void Error(string message)
        {
            if (_log != null)
                _log.Error(message);
        }

        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        protected virtual void OnUpdate() { }
        protected virtual void OnFixedUpdate() { }
        protected virtual void OnLateUpdate() { }
        protected virtual void OnGUI() { }

        public void ScriptEngineInvokeEnable() => OnEnable();
        public void ScriptEngineInvokeDisable() => OnDisable();
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
                _runtimeExceptionHandler?.Invoke(callbackName, ex);
            }
        }
    }

    internal sealed class ScriptLog
    {
        static readonly object WriteLock = new();

        readonly string _relativePath;
        readonly string _logPath;

        public string LogPath => _logPath;

        ScriptLog(string relativePath, string logPath)
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
        }

        public static ScriptLog ForScript(string scriptsDir, string relativePath)
        {
            var logRelativePath = Path.ChangeExtension(relativePath.Replace('/', Path.DirectorySeparatorChar), ".log");
            var logPath = Path.Combine(scriptsDir, "logs", logRelativePath);
            return new ScriptLog(relativePath, logPath);
        }

        public void Info(string message) => Write("Info", message, MelonLogger.Msg);
        public void Warn(string message) => Write("Warn", message, MelonLogger.Warning);
        public void Error(string message) => Write("Error", message, MelonLogger.Error);

        void Write(string level, string message, Action<string> melonWrite)
        {
            var formatted = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            melonWrite($"[{_relativePath}] {message}");

            try
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                lock (WriteLock)
                    File.AppendAllText(_logPath, formatted + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ScriptEngine] Failed to write log for {_relativePath}: {ex.Message}");
            }
        }
    }
}
