using System;
using System.Linq;
using System.Reflection;

namespace ScriptEngine
{
    internal static class UnityRuntime
    {
        static bool _initialized;
        static Type? _gameObjectType;
        static Type? _objectType;
        static ConstructorInfo? _gameObjectStringCtor;
        static MethodInfo? _dontDestroyOnLoad;
        static MethodInfo? _destroy;
        static MethodInfo? _addComponent;

        public static object CreatePersistentGameObject(string name)
        {
            EnsureInitializedOrThrow();
            var gameObject = _gameObjectStringCtor!.Invoke(new object[] { name });
            _dontDestroyOnLoad!.Invoke(null, new[] { gameObject });
            return gameObject;
        }

        public static object AddComponent(object gameObject, Type componentType)
        {
            EnsureInitializedOrThrow();
            return _addComponent!.Invoke(gameObject, new object[] { componentType })!;
        }

        public static void DestroyObject(object unityObject)
        {
            if (!EnsureInitialized())
                return;

            _destroy!.Invoke(null, new[] { unityObject });
        }

        static void EnsureInitializedOrThrow()
        {
            if (!EnsureInitialized())
                throw new InvalidOperationException("Unity runtime types are unavailable.");
        }

        static bool EnsureInitialized()
        {
            if (_initialized)
                return true;

            _gameObjectType = FindType("UnityEngine.GameObject");
            _objectType = FindType("UnityEngine.Object");
            if (_gameObjectType == null || _objectType == null)
                return false;

            _gameObjectStringCtor = _gameObjectType.GetConstructor(new[] { typeof(string) });
            _dontDestroyOnLoad = _objectType.GetMethod("DontDestroyOnLoad", BindingFlags.Public | BindingFlags.Static, null, new[] { _objectType }, null);
            _destroy = _objectType.GetMethod("Destroy", BindingFlags.Public | BindingFlags.Static, null, new[] { _objectType }, null);
            _addComponent = _gameObjectType.GetMethod("AddComponent", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
            _initialized = _gameObjectStringCtor != null && _dontDestroyOnLoad != null && _destroy != null && _addComponent != null;
            return _initialized;
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

    internal static class HarmonyRuntime
    {
        static bool _initialized;
        static Type? _harmonyType;
        static ConstructorInfo? _stringCtor;
        static MethodInfo? _patchAll;
        static MethodInfo? _unpatchSelf;

        public static object Create(string id)
        {
            EnsureInitializedOrThrow();
            return _stringCtor!.Invoke(new object[] { id });
        }

        public static void PatchAll(object harmony, Assembly assembly)
        {
            EnsureInitializedOrThrow();
            _patchAll!.Invoke(harmony, new object[] { assembly });
        }

        public static void UnpatchSelf(object harmony)
        {
            if (!EnsureInitialized())
                return;

            _unpatchSelf!.Invoke(harmony, Array.Empty<object>());
        }

        static void EnsureInitializedOrThrow()
        {
            if (!EnsureInitialized())
                throw new InvalidOperationException("Harmony runtime types are unavailable.");
        }

        static bool EnsureInitialized()
        {
            if (_initialized)
                return true;

            _harmonyType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try { return assembly.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(type => type.FullName == "HarmonyLib.Harmony");
            if (_harmonyType == null)
                return false;

            _stringCtor = _harmonyType.GetConstructor(new[] { typeof(string) });
            _patchAll = _harmonyType.GetMethod("PatchAll", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Assembly) }, null);
            _unpatchSelf = _harmonyType.GetMethod("UnpatchSelf", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _initialized = _stringCtor != null && _patchAll != null && _unpatchSelf != null;
            return _initialized;
        }
    }
}
