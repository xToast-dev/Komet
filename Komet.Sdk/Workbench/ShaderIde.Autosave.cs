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
    private const int MaxAutosaveBytes = 4 << 20, MaxAutosaveWork = 256;

    private readonly Dictionary<string, int> _autosaved = new(StringComparer.Ordinal); // path -> version kept
    private readonly List<(string File, string? Text)> _autosaveLater = []; // closed tabs' drops, failed writes again
    private long _autosaveAt;
    private Task<List<(string File, string? Text)>> _autosaving = Task.FromResult(new List<(string, string?)>());

    private static string AutosaveFolder => Path.Join(StudioLink.KometFolder, ".autosave");

    private static string AutosaveFile(string path) =>
        Path.Join(AutosaveFolder,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))) + ".txt");

    // Per poll: what changed since the last keep is written once typing paused; clean tabs drop theirs
    private void AutosavePoll(long now)
    {
        if (now < _autosaveAt || !_autosaving.IsCompleted) return;

        _autosaveAt = now + AutosaveDelayMs;
        List<(string File, string? Text)> work = [.. _autosaving.Result, .. _autosaveLater]; // what failed is tried again
        _autosaveLater.Clear();
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

        // a later entry of one file wins: an older draft never lands after a newer one or a drop
        work = [.. work.Select((w, i) => (w, i)).GroupBy(p => p.w.File, StringComparer.Ordinal).Select(g => g.Last().w)];
        if (work.Count > 0) _autosaving = Task.Run(() => WriteAutosaves(work));
    }

    // A tab closed: its kept text goes, as its file now holds what it had
    private void AutosaveDrop(string path)
    {
        if (_autosaved.Remove(path) || File.Exists(AutosaveFile(path))) _autosaveLater.Add((AutosaveFile(path), null));
    }

    // The entries that failed, for the next poll; a read-only config folder keeps none
    private static List<(string File, string? Text)> WriteAutosaves(List<(string File, string? Text)> work)
    {
        List<(string File, string? Text)> failed = [];
        try
        {
            _ = Directory.CreateDirectory(AutosaveFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return failed;
        }

        foreach (var entry in work.Bounded(MaxAutosaveWork))
        {
            try
            {
                if (entry.Text is null)
                {
                    File.Delete(entry.File);
                    continue;
                }

                var temp = entry.File + ".tmp";
                File.WriteAllText(temp, entry.Text);
                File.Move(temp, entry.File, true); // never half a file after a crash
            }
            catch (IOException)
            {
                failed.Add(entry);
            }
            catch (UnauthorizedAccessException)
            {
                // a read-only config folder: no autosave
            }
        }

        return failed;
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
