# ScriptEngine

A MelonLoader mod for **Modulus** that hot-reloads C# scripts at runtime — no game restart needed.

Drop a `.cs` file into the `Scripts/` folder, save it, and it compiles and runs within 300ms.

## How it works

ScriptEngine uses [Roslyn](https://github.com/dotnet/roslyn) to compile `.cs` files in-process. Every loaded assembly in the game (Unity engine, MelonLoader, game code, everything) is automatically available as a reference — you don't need a project file or build step.

On save:
1. `FileSystemWatcher` detects the change (debounced 300ms)
2. Previous version's `OnUnload()` is called
3. Roslyn compiles the file in-memory
4. New assembly is loaded and `OnLoad()` is called

## Installation

```
Mods/ScriptEngine.dll
UserLibs/Microsoft.CodeAnalysis.dll
UserLibs/Microsoft.CodeAnalysis.CSharp.dll
UserLibs/System.Collections.Immutable.dll
UserLibs/System.Reflection.Metadata.dll
UserLibs/System.Memory.dll
UserLibs/System.Buffers.dll
UserLibs/System.Numerics.Vectors.dll
UserLibs/System.Runtime.CompilerServices.Unsafe.dll
UserLibs/System.Text.Encoding.CodePages.dll
UserLibs/System.Threading.Tasks.Extensions.dll
```

All files are produced by `dotnet build -c Release` and copied by the deploy step.

## Writing scripts

Place `.cs` files in `<GameDir>/Scripts/`. ScriptEngine scans that folder on startup and watches for changes.

Your script can be any valid C# file. ScriptEngine finds and calls these two static methods if present:

```csharp
public static void OnLoad()   { /* called after compile / hot-reload */ }
public static void OnUnload() { /* called before reload or on file delete */ }
```

All game types, UnityEngine, MelonLoader, and Harmony are available without any imports beyond `using` directives.

### Minimal example

```csharp
using MelonLoader;

public static class Hello
{
    public static void OnLoad() => MelonLogger.Msg("Hello!");
}
```

### Per-frame update (MonoBehaviour)

Scripts don't get `Update()` directly. Attach a `MonoBehaviour` to a persistent `GameObject`:

```csharp
using MelonLoader;
using UnityEngine;

public static class MyScript
{
    static GameObject? _go;

    public static void OnLoad()
    {
        if (_go != null) GameObject.Destroy(_go);
        _go = new GameObject("__MyScript__");
        GameObject.DontDestroyOnLoad(_go);
        _go.AddComponent<MyBehaviour>();
    }

    public static void OnUnload()
    {
        if (_go != null) { GameObject.Destroy(_go); _go = null; }
    }
}

public class MyBehaviour : MonoBehaviour
{
    void Update()
    {
        // runs every frame
    }
}
```

### Harmony patches

```csharp
using HarmonyLib;
using MelonLoader;

public static class MyPatch
{
    static HarmonyLib.Harmony _h = new("mypatch");

    public static void OnLoad()
    {
        _h.UnpatchSelf();
        _h.PatchAll(typeof(MyPatch).Assembly);
    }

    public static void OnUnload() => _h.UnpatchSelf();
}

[HarmonyPatch(typeof(SomeClass), nameof(SomeClass.SomeMethod))]
static class SomeClass_SomeMethod_Patch
{
    static void Prefix() => MelonLogger.Msg("Before SomeMethod!");
}
```

Calling `_h.UnpatchSelf()` at the start of `OnLoad` ensures patches don't stack up on hot-reload.

## Building

Requires .NET SDK 6+.

```bash
cd ScriptEngine
dotnet build -c Release
```

Then copy `bin/Release/netstandard2.0/ScriptEngine.dll` to `Mods/` and the remaining DLLs to `UserLibs/`.

## Project layout

```
ScriptEngine/
  ScriptEngine.csproj   — project file, references MelonLoader + Roslyn
  ScriptEngineMod.cs    — the mod: FSW, compiler, loader
  README.md
```
