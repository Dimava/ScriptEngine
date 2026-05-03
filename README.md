# ScriptEngine

A universal MelonLoader mod that hot-reloads C# scripts at runtime — no game restart needed.

Drop a `.cs` file into the `Scripts/` folder, save it, and it compiles and runs within 300ms.

On first launch, ScriptEngine creates `Scripts/HelloWorld.cs` automatically if the folder has no scripts yet. The sample uses the new `[ScriptEntry]` + `ScriptMod` API.

## How it works

ScriptEngine uses [Roslyn](https://github.com/dotnet/roslyn) to compile `.cs` files in-process. Every loaded assembly in the current MelonLoader game is automatically available as a reference, and ScriptEngine also scans the game's managed DLL directory for anything not yet loaded.

On save:
1. `FileSystemWatcher` detects the change (debounced 300ms)
2. Previous version is unloaded
3. Roslyn compiles the file in-memory
4. New assembly is loaded through the `[ScriptEntry]` / `ScriptMod` loader

## Installation

Download the latest release zip and extract it into the target MelonLoader game's directory.

The release archive contains this layout:

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

If you are building from source, package the release files with `.\scripts\package-release.ps1`.

## Writing scripts

Place `.cs` files in `<GameDir>/Scripts/`. ScriptEngine scans that folder on startup and watches for changes.

Discovery rules:

- Scripts are `.cs` files with exactly one `[ScriptEntry]` class deriving from `ScriptMod`
- Other `.cs` files are ignored
- Directories whose name starts with `.` are ignored
- `bin`, `obj`, and `node_modules` are ignored

ScriptEngine also maintains `<GameDir>/Scripts/ScriptEngine.cfg` to control which scripts are allowed to load:

```toml
[scripts]
enabled = true
enableNewScripts = true
enableNewEvalScripts = true

[scripts."ConnectionHotkey.cs"]
enabled = false
keyCopy = "Ctrl+C"
keyPaste = "Ctrl+V"

[scripts."tetra/TetraRenderer.cs"]
enabled = true
error = "The name \"Foo\" does not exist in the current context (12,8)"
```

- Script keys are paths relative to `Scripts/`, normalized with `/`
- `[scripts].enabled` controls the global script system state
- Missing scripts are added automatically using `enableNewScripts`
- Missing `Eval/*` scripts use `enableNewEvalScripts` instead
- Registered script keys are stored as flat `key...` entries inside the script section
- Deleted scripts are removed from the config automatically
- Editing `ScriptEngine.cfg` while the game is running applies immediately
- Press `F8` in-game to open a simple ScriptEngine window with a global toggle, per-script toggles, and editable keybinding text fields
- `error` is engine-managed and cleared automatically after a successful compile
- Runtime callback exceptions disable the script and write the exception into `error`
- Per-script logs are written to `Scripts/logs/<relative-script-path>.log`
- The `Scripts/logs/` session logs are reset on game launch, not on hot-reload

## Eval scripts

`Scripts/Eval` is for one-shot in-game probes. Keep eval files inert with `[ScriptEval]`; run `Scripts/Eval/Invoke-Eval.ps1` to temporarily switch the first marker to `[ScriptEntry]`, wait for the intentional load failure, restore `[ScriptEval]`, and print the eval script's personal log.

```powershell
& 'Scripts\Eval\Invoke-Eval.ps1' -Script 'Eval/MyEval.cs'
```

Eval scripts should log their result and then throw so ScriptEngine unloads them:

```csharp
using ScriptEngine;

[ScriptEval]
public sealed class MyEval : ScriptMod
{
    protected override void OnEnable()
    {
        Log("EVAL_RESULT ...");
        throw new System.Exception("EVAL_DONE");
    }
}
```

## Writing `ScriptMod` scripts

Recommended format:

```csharp
using HarmonyLib;
using ScriptEngine;

[ScriptEntry]
public sealed class Hello : ScriptMod
{
    protected override void OnEnable()
    {
        Log("After the script was loaded (duplicated log).");
        keys.Register("Copy", "Ctrl+C");
        keys.Register("Paste", "Ctrl+V");
    }

    protected override void OnUpdate()
    {
        if (keys["Copy"].WasPressedThisFrame)
            Log("Copy pressed.");

        if (keys.WasPressed("Paste"))
            Log("Paste pressed.");
    }
}
```

Available `ScriptMod` lifecycle methods:

- `OnEnable`
- `OnDisable`
- `OnUpdate`
- `OnFixedUpdate`
- `OnLateUpdate`
- `OnGUI`

`ScriptMod` also exposes:

- `gameObject`
- `keys.Register("Id", "Ctrl+X")`
- `keys["Id"].WasPressedThisFrame`
- `keys["Id"].IsPressed`
- `keys.WasPressed("Id")`
- `keys.IsDown("Id")`
- `Log(string)`
- `Warn(string)`
- `Error(string)`

Keybinding notes:

- The key id is also the GUI label and the cfg suffix, so `keys.Register("Copy", "Ctrl+C")` writes `keyCopy = "Ctrl+C"`
- Supported combos are a single primary key plus optional `Ctrl`, `Shift`, and/or `Alt`
- Empty bindings are allowed and simply never fire

Load and unload are already logged by ScriptEngine itself, so you usually should not add `Log("Loaded.")` / `Log("Unloaded.")` in `OnEnable` and `OnDisable`.

Harmony patches are applied automatically for `ScriptMod` scripts. Any `[HarmonyPatch]` type in the script file's compiled assembly is patched on load and unpatched on unload.

## Exploring game code

Roslyn compiles against the game's `.dll` files directly — you never need source. But to know what types and methods exist, decompile the game assembly first:

```bash
# requires: dotnet tool install -g ilspycmd
.\scripts\decompile-game.ps1 -GamePath "C:\Path\To\Your\MelonLoaderGame"
```

By default this writes one `.cs` file per class into `<GameDir>\Decompiled\`. Browse them to find types and method names, then reference them in your scripts. Compile errors like `does not contain a definition for 'X'` mean you got a name wrong — check the decompiled file.

## Building

Requires .NET SDK 6+.

ScriptEngine resolves MelonLoader from the official NuGet package at build time.

```powershell
dotnet restore .\ScriptEngine\ScriptEngine.csproj
dotnet build .\ScriptEngine\ScriptEngine.csproj -c Release
```

To restore, build, and create a release zip in one step:

```powershell
.\scripts\package-release.ps1 -Version 1.0.0
```

This creates `release/ScriptEngine-1.0.0.zip`, ready for GitHub Releases or Nexus Mods.

For local development, you can hard-link the current build output into the game's `Mods/` and `UserLibs/` directories:

```powershell
.\scripts\link-dev-build.ps1 -GamePath "C:\Path\To\Your\MelonLoaderGame"
```

## Project layout

```
ScriptEngine/
  ScriptEngine.csproj   — project file, references MelonLoader + Roslyn
  ScriptEngineMod.cs    — the mod: FSW, config, UI
  ScriptLoaders.cs      — discovery, compiler, and `[ScriptEntry]` loader
  ScriptApi.cs          — shared script base and per-script log sink
  ScriptRuntime.cs      — reflection wrappers for Unity/Harmony host integration
  README.md
```
