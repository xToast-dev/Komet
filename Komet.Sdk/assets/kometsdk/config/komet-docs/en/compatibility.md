# Resolve compatibility issues

[Troubleshooting](docs-problems)

## Capture the finding

1. Reproduce the problem with the same game, Komet and mod versions.
2. Open Komet > Compatibility and choose Refresh findings. Individual entries copy their details; Copy compatibility
   report includes every captured entry.
3. Include the game version, Komet version, mod versions, client-main.log and a debug protocol.
4. Reopen or refresh the page after activating or pausing a shader mod. The report is a snapshot.

## Interpret the cause

| Finding                         | Meaning and next step                                                                                                                                                    |
|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Subpath finding                 | A feature can remain Active while a protected subpath uses the original behaviour. Read the method evidence for the affected path.                                       |
| HeldOff                         | A mod requests original behaviour. HeldBy and Reason identify the requester and reason. This is often deliberate cooperation. Check whether the hold is still necessary. |
| StoodDown                       | A guard has made the feature step aside. Method details show existing Harmony patches or missing targets. Check the log when no method evidence is available.            |
| EngineChanged                   | Expected methods or their fingerprint differ. A different game version can cause this too. Compare expected and found values and the full method signatures.             |
| Failed                          | Feature code reported an error. Inspect the exception and call stack in its details or the log.                                                                          |
| External Harmony patch on Komet | A Komet method is patched under another Harmony ID. This alone does not prove a fault. Inspect the target, patch method and patch kind.                                  |

Harmony IDs are identifiers chosen by patch authors. They need not match the mod name. A different package checksum
likewise does not establish who changed a file. This display cannot fully detect changes made without Harmony.

## Repair Harmony conflicts

The report names the target signature, Harmony ID, patch method, patch kind and, where available, priority. First map
the Harmony ID to its patch in your code or the debug protocol.

- Check whether a prefix skips the original or a transpiler assumes behaviour replaced by Komet.
- Prefer KAPI (`Komet.Api`) where it covers your case. It is versioned and keeps its promise within a major version;
  internal Komet methods can change between any two versions.
- Use a postfix when sufficient instead of a replacing prefix. HarmonyBefore and HarmonyAfter order patches but do not
  resolve contradictory behaviour.
- When original behaviour is required, hold only the affected feature with `komet.Features.HoldOff`. Acquire and release
  the hold on the main thread, including after a partial initialization failure.

```csharp
// in your KAPI bridge, with the connection from Kapi.Connect(system, "1.0")
KapiHold? hold;

void Attach(KapiMod komet)
{
    hold = komet.Features.HoldOff("FrustumSweep",
        "Our render pass needs the original culling.");
}

void Detach()
{
    hold?.Dispose(); // komet.Dispose() and the world's end release it too
    hold = null;
}
```

## Repair changed methods

For EngineChanged, first check the supported game version and overloaded-method resolution. Supply full parameter lists
and handle missing targets. Update fingerprints only after reviewing and testing the new engine code. Do not bypass the
guard by copying the found value without checking it.

For Failed, repair the first relevant frame in your code. Uninstall must safely release partially created resources.
Once installation began, Komet invokes cleanup once on failure or world close.

## Verify the repair

Test the affected scene with both mods, with the affected Komet feature disabled, and after changing worlds. For shader
mods also test activation, pausing and reactivation. Refresh the report afterwards: resolved conflicts should disappear
or remain clearly identifiable as deliberate holds. Include a fresh report and log with the fix.
