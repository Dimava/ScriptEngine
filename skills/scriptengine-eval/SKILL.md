---
name: scriptengine-eval
description: Use when an agent needs to run a one-shot ScriptEngine eval inside Modulus by creating or editing a Scripts/Eval/*.cs file, activating it through Invoke-Eval.ps1, reading the script's personal log, and relying on an intentional throw to unload.
---

# ScriptEngine Eval

Use `Scripts/Eval` for one-shot in-game probes. Eval files stay inert with `[ScriptEval]`; `Invoke-Eval.ps1` temporarily changes the first marker to `[ScriptEntry]`, waits for ScriptEngine to log the intentional load failure, restores `[ScriptEval]`, and prints `Scripts/logs/Eval/<script>.log`.

Template:

```csharp
using ScriptEngine;

[ScriptEval]
public sealed class MyEval : ScriptMod
{
    protected override void OnEnable()
    {
        Log("EVAL_RESULT ...");
        Error("optional error-channel detail");
        throw new System.Exception("EVAL_DONE");
    }
}
```

Run:

```powershell
& 'Scripts\Eval\Invoke-Eval.ps1' -Script 'Eval/MyEval.cs'
```

Notes:

- `enableNewEvalScripts = true` auto-enables newly discovered `Eval/*` script config entries.
- Do not leave eval code as `[ScriptEntry]`; keep `[ScriptEval]` outside the brief invocation window.
- The useful output is the personal log, not the source file.
- Compile failures and throws are written to the personal log and global `Scripts/logs/errors.log`.
