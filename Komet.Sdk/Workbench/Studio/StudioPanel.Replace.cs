using System.Text;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Replace in files: the search's text, in any case as the search finds it, goes into the project copies among the
// hits. Game files and installed mods stay as they are; an open tab takes it as one undo step, a closed file is written.
internal sealed partial class StudioPanel
{
    private void ReplaceAll()
    {
        var q = _hitText;
        if (q.Length < 2) return;

        var files = _found.Where(l => l.Heading && Editable(l.Path)).Select(l => l.Path)
            .Distinct(StringComparer.Ordinal).ToArray();
        var count = 0;
        foreach (var file in files.Bounded(MaxSearchFiles))
        {
            count += Occurrences(Text(file) ?? "", q);
        }

        if (count == 0)
        {
            host.Toast(L("replace-none"));
            return;
        }

        var with = _replace.Text;
        host.Modal.Confirm(L("replace-all"), L("replace-confirm", count, files.Length, q, with), L("replace"), () =>
        {
            var (done, changed) = (0, 0);
            foreach (var file in files.Bounded(MaxSearchFiles))
            {
                if (Text(file) is not { } text) continue;

                var n = Occurrences(text, q);
                var result = Replaced(text, q, with);
                if (n == 0 || result == text) continue;

                var ok = FindTab(file) is { } open
                    ? open.Replace(0, open.Text.Length, result)
                    : StudioLink.Write(file, result);
                if (!ok) continue;

                (done, changed) = (done + n, changed + 1);
            }

            host.Toast(L("replaced", done, changed));
            _queryVersion = -1;
            Refresh();
        });
    }

    // Only the project copies (never the game's assets or a mod's files)
    private static bool Editable(string path) =>
        !InstalledShaders.Packed(path) && StudioLink.Within(path, StudioLink.ProjectsFolder) &&
        !StudioLink.Hidden(path) &&
        StudioLink.CanWrite(path);

    private string? Text(string file)
    {
        if (FindTab(file) is { } open) return open.Text;

        return StudioLink.Read(file, out var text, out _) ? text : null;
    }

    private static int Occurrences(string text, string q)
    {
        var (count, at) = (0, text.IndexOf(q, StringComparison.OrdinalIgnoreCase));
        for (var guard = 0; guard < StudioLink.MaxText; guard++)
        {
            if (at < 0) break;

            count++;
            at = text.IndexOf(q, at + q.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }

    private static string Replaced(string text, string q, string with)
    {
        var built = new StringBuilder(text.Length);
        var (from, at) = (0, text.IndexOf(q, StringComparison.OrdinalIgnoreCase));
        for (var guard = 0; guard < StudioLink.MaxText; guard++)
        {
            if (at < 0) break;

            _ = built.Append(text, from, at - from).Append(with);
            from = at + q.Length;
            at = text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
        }

        return built.Append(text, from, text.Length - from).ToString();
    }
}
