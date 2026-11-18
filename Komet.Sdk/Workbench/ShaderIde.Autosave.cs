using System.Security.Cryptography;
using System.Text;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Autosave: every open tab with unsaved text is kept in ModConfig/komet/.autosave a moment after the last edit, apart from
// its file (writing the file would start an AUTO build and reload the game's shaders on every pause in typing). A crash or
// a closed game loses nothing: the next time the file opens, the kept text comes back as an unsaved draft. A tab that is
// saved, built or closed clean drops its kept text. Written on a worker; one file per path, named by its hash.
internal sealed partial class ShaderIde
{
    private const long AutosaveDelayMs = 1500;
    private const int MaxAutosaveBytes = 4 << 20;

    private readonly Dictionary<string, int> _autosaved = new(StringComparer.Ordinal); // path -> version kept
    private long _autosaveAt;
    private Task _autosaving = Task.CompletedTask;

    private static string AutosaveFolder => Path.Join(StudioLink.KometFolder, ".autosave");

    private static string AutosaveFile(string path) =>
        Path.Join(AutosaveFolder,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))) + ".txt");

    // Per poll: what changed since the last keep is written once typing paused; clean tabs drop theirs
    private void AutosavePoll(long now)
    {
        if (now < _autosaveAt || !_autosaving.IsCompleted) return;

        _autosaveAt = now + AutosaveDelayMs;
        List<(string File, string? Text)> work = [];
        for (var i = 0; i < Math.Min(_tabs.Count, MaxTabs + 1); i++)
        {
            var tab = _tabs[i];
            if (tab.ReadOnly) continue;

            var kept = _autosaved.TryGetValue(tab.Path, out var version);
            if (tab.Dirty && (!kept || version != tab.Version) && tab.Text.Length <= MaxAutosaveBytes)
            {
                _autosaved[tab.Path] = tab.Version;
                work.Add((AutosaveFile(tab.Path), tab.Path + "\n" + tab.Text));
            }
            else if (!tab.Dirty && kept)
            {
                _ = _autosaved.Remove(tab.Path);
                work.Add((AutosaveFile(tab.Path), null));
            }
        }

        if (work.Count > 0) _autosaving = Task.Run(() => WriteAutosaves(work));
    }

    private static void WriteAutosaves(List<(string File, string? Text)> work)
    {
        try
        {
            _ = Directory.CreateDirectory(AutosaveFolder);
            foreach (var (file, text) in work.Bounded(MaxTabs + 1))
            {
                if (text is null)
                {
                    File.Delete(file);
                    continue;
                }

                var temp = file + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, file, true); // never half a file after a crash
            }
        }
        catch (IOException)
        {
            // the next poll tries again
        }
        catch (UnauthorizedAccessException)
        {
            // a read-only config folder: no autosave
        }
    }

    // A file opens: its kept draft, when it differs from the disk, null otherwise
    private static string? Recovered(string path)
    {
        try
        {
            var file = AutosaveFile(path);
            if (!File.Exists(file)) return null;

            var kept = File.ReadAllText(file);
            var end = kept.IndexOf('\n', StringComparison.Ordinal);
            return end > 0 && StudioLink.PathEquals(kept[..end], path) ? kept[(end + 1)..] : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A new tab: the kept draft comes back unsaved, with a toast that says so
    private ShaderDocument Opening(string path, string disk, bool readOnly)
    {
        if (readOnly || Recovered(path) is not { } draft || draft == disk) return new ShaderDocument(path, disk, disk, readOnly);

        Toast(L("autosave-restored", StudioLink.Name(path)));
        var document = new ShaderDocument(path, disk, draft, false);
        _autosaved[path] = document.Version;
        return document;
    }
}
