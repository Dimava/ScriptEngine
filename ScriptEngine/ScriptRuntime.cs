using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ScriptEngine
{
    public static class UnityRuntime
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

    public static class HarmonyRuntime
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

    public readonly struct ScriptKeyChord
    {
        public static readonly ScriptKeyChord Unbound = new(ctrl: false, shift: false, alt: false, keyCode: null);

        readonly bool _ctrl;
        readonly bool _shift;
        readonly bool _alt;
        readonly object? _keyCode;

        public ScriptKeyChord(bool ctrl, bool shift, bool alt, object? keyCode)
        {
            _ctrl = ctrl;
            _shift = shift;
            _alt = alt;
            _keyCode = keyCode;
        }

        public bool IsBound => _keyCode != null;

        public bool IsPressed()
        {
            if (_keyCode == null || !InputRuntime.AreModifierStatesExact(_ctrl, _shift, _alt))
                return false;

            return InputRuntime.GetKey(_keyCode);
        }

        public bool WasPressedThisFrame()
        {
            if (_keyCode == null || !InputRuntime.AreModifierStatesExact(_ctrl, _shift, _alt))
                return false;

            return InputRuntime.GetKeyDown(_keyCode);
        }
    }

    public static class InputRuntime
    {
        static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = "Escape",
            ["Escape"] = "Escape",
            ["Enter"] = "Return",
            ["Return"] = "Return",
            ["Space"] = "Space",
            ["Spacebar"] = "Space",
            ["Tab"] = "Tab",
            ["Backspace"] = "Backspace",
            ["Delete"] = "Delete",
            ["Del"] = "Delete",
            ["Insert"] = "Insert",
            ["Ins"] = "Insert",
            ["Home"] = "Home",
            ["End"] = "End",
            ["PageUp"] = "PageUp",
            ["PgUp"] = "PageUp",
            ["PageDown"] = "PageDown",
            ["PgDn"] = "PageDown",
            ["Up"] = "UpArrow",
            ["Down"] = "DownArrow",
            ["Left"] = "LeftArrow",
            ["Right"] = "RightArrow",
            ["Minus"] = "Minus",
            ["Equals"] = "Equals",
            ["Comma"] = "Comma",
            ["Period"] = "Period",
            ["Dot"] = "Period",
            ["Slash"] = "Slash",
            ["Backslash"] = "Backslash",
            ["Semicolon"] = "Semicolon",
            ["Quote"] = "Quote",
            ["Apostrophe"] = "Quote",
            ["LeftBracket"] = "LeftBracket",
            ["RightBracket"] = "RightBracket",
            ["BackQuote"] = "BackQuote",
            ["Backtick"] = "BackQuote",
            ["Grave"] = "BackQuote",
            ["Tilde"] = "BackQuote",
        };

        static readonly Dictionary<string, string> DisplayAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Return"] = "Enter",
            ["UpArrow"] = "Up",
            ["DownArrow"] = "Down",
            ["LeftArrow"] = "Left",
            ["RightArrow"] = "Right",
        };

        static readonly Dictionary<string, object> KeyCodeCache = new(StringComparer.OrdinalIgnoreCase);

        static bool _initialized;
        static Type? _inputType;
        static Type? _keyCodeType;
        static MethodInfo? _getKeyMethod;
        static MethodInfo? _getKeyDownMethod;
        static object? _leftControl;
        static object? _rightControl;
        static object? _leftShift;
        static object? _rightShift;
        static object? _leftAlt;
        static object? _rightAlt;

        public static bool TryParseBindingText(string? bindingText, out ScriptKeyChord chord, out string normalizedBinding, out string error)
        {
            normalizedBinding = "";
            error = "";
            chord = ScriptKeyChord.Unbound;

            if (string.IsNullOrWhiteSpace(bindingText))
                return true;

            if (!EnsureInitialized())
            {
                error = "Unity input runtime is unavailable.";
                return false;
            }

            bool ctrl = false;
            bool shift = false;
            bool alt = false;
            object? keyCode = null;
            string? keyDisplay = null;

            var tokens = bindingText.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawToken in tokens)
            {
                var token = rawToken.Trim();
                if (string.IsNullOrEmpty(token))
                    continue;

                if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    if (ctrl)
                    {
                        error = "Ctrl is duplicated.";
                        return false;
                    }

                    ctrl = true;
                    continue;
                }

                if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    if (shift)
                    {
                        error = "Shift is duplicated.";
                        return false;
                    }

                    shift = true;
                    continue;
                }

                if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    if (alt)
                    {
                        error = "Alt is duplicated.";
                        return false;
                    }

                    alt = true;
                    continue;
                }

                if (keyCode != null)
                {
                    error = "Only one non-modifier key is allowed.";
                    return false;
                }

                if (!TryResolvePrimaryKey(token, out keyCode, out keyDisplay))
                {
                    error = $"Unknown key '{token}'.";
                    return false;
                }
            }

            if (keyCode == null || keyDisplay == null)
            {
                error = "A non-modifier key is required.";
                return false;
            }

            normalizedBinding = BuildNormalizedBinding(ctrl, shift, alt, keyDisplay);
            chord = new ScriptKeyChord(ctrl, shift, alt, keyCode);
            return true;
        }

        public static bool AreModifierStatesExact(bool ctrl, bool shift, bool alt)
        {
            if (!EnsureInitialized())
                return false;

            return IsCtrlDown() == ctrl
                && IsShiftDown() == shift
                && IsAltDown() == alt;
        }

        public static bool GetKey(object keyCode)
        {
            if (!EnsureInitialized())
                return false;

            return (bool)_getKeyMethod!.Invoke(null, new[] { keyCode })!;
        }

        public static bool GetKeyDown(object keyCode)
        {
            if (!EnsureInitialized())
                return false;

            return (bool)_getKeyDownMethod!.Invoke(null, new[] { keyCode })!;
        }

        static bool EnsureInitialized()
        {
            if (_initialized)
                return true;

            _inputType = FindType("UnityEngine.Input");
            _keyCodeType = FindType("UnityEngine.KeyCode");
            if (_inputType == null || _keyCodeType == null)
                return false;

            _getKeyMethod = _inputType.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new[] { _keyCodeType }, null);
            _getKeyDownMethod = _inputType.GetMethod("GetKeyDown", BindingFlags.Public | BindingFlags.Static, null, new[] { _keyCodeType }, null);
            if (_getKeyMethod == null || _getKeyDownMethod == null)
                return false;

            if (!TryGetKeyCode("LeftControl", out _leftControl)
                || !TryGetKeyCode("RightControl", out _rightControl)
                || !TryGetKeyCode("LeftShift", out _leftShift)
                || !TryGetKeyCode("RightShift", out _rightShift)
                || !TryGetKeyCode("LeftAlt", out _leftAlt)
                || !TryGetKeyCode("RightAlt", out _rightAlt))
                return false;

            _initialized = true;
            return true;
        }

        static bool TryResolvePrimaryKey(string token, out object? keyCode, out string? keyDisplay)
        {
            keyCode = null;
            keyDisplay = null;

            var normalizedToken = token.Trim();
            if (normalizedToken.Length == 1 && char.IsLetter(normalizedToken[0]))
            {
                var enumName = char.ToUpperInvariant(normalizedToken[0]).ToString();
                if (!TryGetKeyCode(enumName, out keyCode))
                    return false;

                keyDisplay = enumName;
                return true;
            }

            if (normalizedToken.Length == 1 && char.IsDigit(normalizedToken[0]))
            {
                var enumName = $"Alpha{normalizedToken[0]}";
                if (!TryGetKeyCode(enumName, out keyCode))
                    return false;

                keyDisplay = normalizedToken;
                return true;
            }

            if (normalizedToken.Length > 1 && normalizedToken[0] == 'F' && int.TryParse(normalizedToken.Substring(1), out var functionKey) && functionKey >= 1 && functionKey <= 24)
            {
                var enumName = $"F{functionKey}";
                if (!TryGetKeyCode(enumName, out keyCode))
                    return false;

                keyDisplay = enumName;
                return true;
            }

            var aliasValue = KeyAliases.TryGetValue(normalizedToken, out var aliasedName)
                ? aliasedName
                : normalizedToken;
            if (!TryGetKeyCode(aliasValue, out keyCode))
                return false;

            keyDisplay = DisplayAliases.TryGetValue(aliasValue, out var aliasedDisplay)
                ? aliasedDisplay
                : aliasValue;
            return true;
        }

        static bool TryGetKeyCode(string enumName, out object? keyCode)
        {
            keyCode = null;
            if (!EnsureKeyCodeType())
                return false;

            if (KeyCodeCache.TryGetValue(enumName, out var cachedKeyCode))
            {
                keyCode = cachedKeyCode;
                return true;
            }

            try
            {
                keyCode = Enum.Parse(_keyCodeType!, enumName, ignoreCase: true);
                KeyCodeCache[enumName] = keyCode;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool EnsureKeyCodeType()
        {
            _keyCodeType ??= FindType("UnityEngine.KeyCode");
            return _keyCodeType != null;
        }

        static bool IsCtrlDown() => GetKey(_leftControl!) || GetKey(_rightControl!);

        static bool IsShiftDown() => GetKey(_leftShift!) || GetKey(_rightShift!);

        static bool IsAltDown() => GetKey(_leftAlt!) || GetKey(_rightAlt!);

        static string BuildNormalizedBinding(bool ctrl, bool shift, bool alt, string keyDisplay)
        {
            var parts = new List<string>(4);
            if (ctrl)
                parts.Add("Ctrl");
            if (shift)
                parts.Add("Shift");
            if (alt)
                parts.Add("Alt");

            parts.Add(keyDisplay);
            return string.Join("+", parts);
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
                catch
                {
                }
            }

            return null;
        }
    }
}
