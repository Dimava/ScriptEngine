using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MelonLoader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

[assembly: MelonInfo(typeof(ScriptEngine.ScriptEngineMod), "ScriptEngine", "1.0.0", "local")]
[assembly: MelonGame()]

namespace ScriptEngine
{
    public class ScriptEngineMod : MelonMod
    {
        static string ScriptsDir = null!;
        static string GameDir = null!;

        // track loaded scripts: file path -> (assembly, onUnload action)
        static readonly Dictionary<string, LoadedScript> _loaded = new();
        static FileSystemWatcher _watcher = null!;

        public override void OnInitializeMelon()
        {
            GameDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName)!;
            ScriptsDir = Path.Combine(GameDir, "Scripts");
            Directory.CreateDirectory(ScriptsDir);

            LoggerInstance.Msg($"ScriptEngine watching: {ScriptsDir}");

            // Load all existing scripts
            foreach (var file in Directory.GetFiles(ScriptsDir, "*.cs", SearchOption.TopDirectoryOnly))
                LoadScript(file);

            // Watch for changes
            _watcher = new FileSystemWatcher(ScriptsDir, "*.cs")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
        }

        // Debounce: FSW fires multiple events per save
        static readonly Dictionary<string, Timer> _debounce = new();

        static void OnFileEvent(object _, FileSystemEventArgs e)
        {
            if (_debounce.TryGetValue(e.FullPath, out var t)) t.Dispose();
            _debounce[e.FullPath] = new Timer(_ =>
            {
                _debounce.Remove(e.FullPath);
                MelonLogger.Msg($"[ScriptEngine] Reloading: {Path.GetFileName(e.FullPath)}");
                LoadScript(e.FullPath);
            }, null, 300, Timeout.Infinite);
        }

        static void OnFileDeleted(object _, FileSystemEventArgs e) => UnloadScript(e.FullPath);

        static void OnFileRenamed(object _, RenamedEventArgs e)
        {
            UnloadScript(e.OldFullPath);
            if (e.FullPath.EndsWith(".cs")) LoadScript(e.FullPath);
        }

        static void LoadScript(string path)
        {
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
            var managedDir = Path.Combine(GameDir, "Modulus_Data", "Managed");
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

            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"  {d.GetMessage()} ({d.Location.GetLineSpan().StartLinePosition})");
                MelonLogger.Error($"[ScriptEngine] Compile errors in {Path.GetFileName(path)}:\n{string.Join("\n", errors)}");
                return null;
            }

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }

    class LoadedScript
    {
        public Assembly Assembly;
        public Action? OnUnload;
        public LoadedScript(Assembly assembly, Action? onUnload) { Assembly = assembly; OnUnload = onUnload; }
    }
}
