using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScriptEngine
{
    public enum ScriptKind
    {
        Attribute,
    }

    public sealed class DiscoveredScript
    {
        public string FullPath = "";
        public string RelativePath = "";
        public ScriptKind Kind;
    }

    public sealed class LoadedScript
    {
        public string FullPath = "";
        public string RelativePath = "";
        public ScriptKind Kind;
        public Assembly Assembly = null!;
        public ScriptModBase? Mod;
        public Action? OnUnload;
        public ScriptLog Log = null!;
    }

    public static class ScriptDiscovery
    {
        static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin",
            "obj",
            "node_modules",
        };

        public static Dictionary<string, DiscoveredScript> GetCurrentScripts(string scriptsDir)
        {
            var scripts = new Dictionary<string, DiscoveredScript>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateScriptFiles(scriptsDir, scriptsDir))
            {
                if (!TryGetRelativeScriptPath(scriptsDir, file, out var relativePath))
                    continue;

                string source;
                try
                {
                    source = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                var kind = ClassifySource(file, source);
                if (kind == null)
                    continue;

                scripts[relativePath] = new DiscoveredScript
                {
                    FullPath = Path.GetFullPath(file),
                    RelativePath = relativePath,
                    Kind = kind.Value,
                };
            }

            return scripts;
        }

        public static bool TryGetRelativeScriptPath(string scriptsDir, string path, out string relativePath)
        {
            relativePath = "";
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(scriptsDir, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            var normalized = relative.Replace('\\', '/');
            if (IsIgnoredRelativePath(normalized))
                return false;

            relativePath = normalized;
            return true;
        }

        static IEnumerable<string> EnumerateScriptFiles(string scriptsDir, string currentDir)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(currentDir, "*.cs", SearchOption.TopDirectoryOnly); }
            catch { yield break; }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly); }
            catch { yield break; }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (ShouldSkipDirectory(name))
                    continue;

                foreach (var file in EnumerateScriptFiles(scriptsDir, directory))
                    yield return file;
            }
        }

        static bool ShouldSkipDirectory(string name) =>
            name.StartsWith(".", StringComparison.Ordinal) || IgnoredDirectoryNames.Contains(name);

        static bool IsIgnoredRelativePath(string relativePath)
        {
            var parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (ShouldSkipDirectory(parts[i]))
                    return true;
            }

            return false;
        }

        static ScriptKind? ClassifySource(string path, string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source, path: path);
            var root = tree.GetRoot();

            if (ContainsScriptEntry(root))
                return ScriptKind.Attribute;

            return null;
        }

        static bool ContainsScriptEntry(SyntaxNode root) =>
            root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Any(HasScriptEntryAttribute);

        static bool HasScriptEntryAttribute(ClassDeclarationSyntax classDeclaration) =>
            classDeclaration.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute =>
                {
                    var name = attribute.Name.ToString();
                    return name.EndsWith("ScriptEntry", StringComparison.Ordinal)
                        || name.EndsWith("ScriptEntryAttribute", StringComparison.Ordinal);
                });
    }

    public sealed class ScriptCompiler
    {
        const string InjectedScriptApiSource =
@"using System;
using UnityEngine;

namespace ScriptEngine
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ScriptEntryAttribute : Attribute
    {
    }

    public abstract class ScriptMod : ScriptModBase
    {
        public GameObject gameObject => (GameObject)_gameObject;

        protected static T FindActiveObjectOfType<T>() where T : UnityEngine.Object
        {
            foreach (var obj in Resources.FindObjectsOfTypeAll<T>())
            {
                if (obj is Component component && component.gameObject.activeInHierarchy)
                    return obj;
            }
            return default;
        }
    }

    public sealed class ScriptHostBehaviour : MonoBehaviour
    {
        public ScriptModBase Mod = null!;

        void Update() => Mod.ScriptEngineInvokeUpdate();
        void FixedUpdate() => Mod.ScriptEngineInvokeFixedUpdate();
        void LateUpdate() => Mod.ScriptEngineInvokeLateUpdate();
        void OnGUI() => Mod.ScriptEngineInvokeGUI();
    }
}";

        public ScriptCompiler()
        {
        }

        public Assembly? Compile(DiscoveredScript script, ScriptLog log, out string? errorText)
        {
            errorText = null;

            string source;
            try
            {
                source = File.ReadAllText(script.FullPath);
            }
            catch (Exception ex)
            {
                log.Error($"Read failed: {ex.Message}");
                errorText = ex.Message;
                return null;
            }

            DeleteLegacyCompileLog(script.FullPath);

            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(source, path: script.FullPath),
            };
            if (script.Kind == ScriptKind.Attribute)
                trees.Add(CSharpSyntaxTree.ParseText(InjectedScriptApiSource, path: "__ScriptEngine_Injected.cs"));

            var compilation = CSharpCompilation.Create(
                assemblyName: Path.GetFileNameWithoutExtension(script.FullPath) + "_" + DateTime.Now.Ticks,
                syntaxTrees: trees,
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: true));

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(FormatDiagnostic)
                    .ToArray();
                errorText = string.Join("\n", errors);
                log.Error("Compile errors:");
                foreach (var error in errors)
                    log.Error(error);
                return null;
            }

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }

        string FormatDiagnostic(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            var path = span.Path;
            var fileName = string.IsNullOrEmpty(path) ? "unknown" : Path.GetFileName(path);
            var line = span.StartLinePosition.Line + 1;
            var character = span.StartLinePosition.Character + 1;
            return $"{fileName}: {diagnostic.GetMessage()} ({line},{character})";
        }

        IEnumerable<MetadataReference> GetMetadataReferences()
        {
            var refs = new List<MetadataReference>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.IsNullOrEmpty(asm.Location))
                        refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch { }
            }

            var managedDir = MelonLoader.Utils.MelonEnvironment.UnityGameManagedDirectory;
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
                    {
                        try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                        catch { }
                    }
                }
            }

            return refs;
        }

        static void DeleteLegacyCompileLog(string scriptPath)
        {
            try
            {
                var legacyLogPath = Path.ChangeExtension(scriptPath, ".log");
                if (File.Exists(legacyLogPath))
                    File.Delete(legacyLogPath);
            }
            catch { }
        }
    }

    public interface IScriptLoader
    {
        ScriptKind Kind { get; }
        LoadedScript? Load(DiscoveredScript script, Assembly assembly, ScriptLog log, Action<string, string, Exception> runtimeExceptionHandler, out string? errorText);
    }

    public sealed class AttributeScriptLoader : IScriptLoader
    {
        public ScriptKind Kind => ScriptKind.Attribute;

        public LoadedScript? Load(DiscoveredScript script, Assembly assembly, ScriptLog log, Action<string, string, Exception> runtimeExceptionHandler, out string? errorText)
        {
            errorText = null;

            var entryTypes = assembly.GetTypes()
                .Where(type =>
                    !type.IsAbstract
                    && typeof(ScriptModBase).IsAssignableFrom(type)
                    && HasScriptEntryAttribute(type))
                .ToArray();
            if (entryTypes.Length != 1)
            {
                errorText = entryTypes.Length == 0
                    ? "Expected exactly one [ScriptEntry] class deriving from ScriptMod."
                    : "Found multiple [ScriptEntry] classes. Only one is allowed per file.";
                log.Error(errorText);
                return null;
            }

            var entryType = entryTypes[0];
            if (!entryType.IsPublic)
            {
                errorText = "[ScriptEntry] class must be public.";
                log.Error(errorText);
                return null;
            }

            object? gameObject = null;
            object? harmony = null;
            object? hostBehaviour = null;
            ScriptModBase? mod = null;
            try
            {
                harmony = HarmonyRuntime.Create("scriptengine." + script.RelativePath.Replace('/', '.').Replace('\\', '.'));
                gameObject = UnityRuntime.CreatePersistentGameObject($"__ScriptEngine::{script.RelativePath}__");
                ScriptModBase.PendingInit = new ScriptInitContext
                {
                    ScriptId = script.RelativePath,
                    Log = log,
                    GameObject = gameObject,
                    RuntimeExceptionHandler = (callbackName, ex) => runtimeExceptionHandler(script.RelativePath, callbackName, ex),
                    BindingRegistrar = (bindingId, defaultBinding) => ScriptEngineMod.RegisterScriptKeyBinding(script.RelativePath, bindingId, defaultBinding),
                    ConfigRegistrar = (configId, defaultValue) => ScriptEngineMod.RegisterScriptConfigValue(script.RelativePath, configId, defaultValue),
                    ConfigSetter = (configId, rawValue) => ScriptEngineMod.SetScriptConfigValue(script.RelativePath, configId, rawValue),
                };
                try
                {
                    mod = (ScriptModBase)Activator.CreateInstance(entryType)!;
                }
                finally
                {
                    ScriptModBase.PendingInit = null;
                }

                var hostType = assembly.GetType("ScriptEngine.ScriptHostBehaviour", throwOnError: false);
                if (hostType == null)
                    throw new InvalidOperationException("Injected ScriptHostBehaviour type was not found.");

                hostBehaviour = UnityRuntime.AddComponent(gameObject, hostType);
                var modField = hostType.GetField("Mod", BindingFlags.Public | BindingFlags.Instance);
                if (modField == null)
                    throw new InvalidOperationException("ScriptHostBehaviour.Mod field was not found.");

                modField.SetValue(hostBehaviour, mod);
                HarmonyRuntime.PatchAll(harmony, assembly);
                mod.ScriptEngineInvokeEnable();
            }
            catch (TargetInvocationException ex)
            {
                errorText = ex.InnerException?.ToString() ?? ex.ToString();
                log.Error($"OnEnable failed: {errorText}");
                CleanupFailedLoad(mod, hostBehaviour, gameObject, harmony, log);
                return null;
            }
            catch (Exception ex)
            {
                errorText = ex.ToString();
                log.Error($"Failed to load attribute script: {errorText}");
                CleanupFailedLoad(mod, hostBehaviour, gameObject, harmony, log);
                return null;
            }

            log.Info("Loaded.");
            return new LoadedScript
            {
                FullPath = script.FullPath,
                RelativePath = script.RelativePath,
                Kind = script.Kind,
                Assembly = assembly,
                Mod = mod!,
                Log = log,
                OnUnload = () =>
                {
                    try
                    {
                        mod.ScriptEngineInvokeDisable();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"OnDisable failed: {ex}");
                    }

                    try { HarmonyRuntime.UnpatchSelf(harmony); }
                    catch (Exception ex) { log.Error($"Unpatch failed: {ex}"); }

                    try
                    {
                        mod.ScriptEngineClearHostObject();
                        UnityRuntime.DestroyObject(gameObject);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Destroy failed: {ex}");
                    }

                    log.Info("Unloaded.");
                }
            };
        }

        static bool HasScriptEntryAttribute(Type type) =>
            type.GetCustomAttributes(inherit: false)
                .Any(attribute =>
                {
                    var name = attribute.GetType().FullName ?? "";
                    return name == "ScriptEngine.ScriptEntryAttribute" || name.EndsWith(".ScriptEntryAttribute", StringComparison.Ordinal);
                });

        static void CleanupFailedLoad(ScriptModBase? mod, object? hostBehaviour, object? gameObject, object? harmony, ScriptLog log)
        {
            if (mod != null)
            {
                try { mod.ScriptEngineInvokeDisable(); }
                catch { }
            }

            try
            {
                if (harmony != null)
                    HarmonyRuntime.UnpatchSelf(harmony);
            }
            catch (Exception ex)
            {
                log.Error($"Cleanup unpatch failed: {ex}");
            }

            try
            {
                if (gameObject != null)
                    UnityRuntime.DestroyObject(gameObject);
            }
            catch (Exception ex)
            {
                log.Error($"Cleanup failed: {ex}");
            }

            mod?.ScriptEngineClearHostObject();
        }
    }
}
