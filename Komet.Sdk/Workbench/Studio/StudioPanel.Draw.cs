using System.Globalization;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The panel's head (PROBLEMS n · OUTPUT · INSPECTOR, ×; docked: the view's name) and its bodies as rows
internal sealed partial class StudioPanel
{
    public void Draw(HudUi ui, double x, double y, double w, double h)
    {
        Actions(ui);
        ui.Fill(x, y, w, h, host.Ground);
        ui.Fill(x, y, w, 1, HudUi.Divider);
        if (!docked) host.Hits.Region(x, y - ui.Px(3), w, ui.Px(6), _split!);
        var head = ui.Px(HeadHeight);
        Head(ui, x, y, w, head);
        var top = y + head;
        _lines.Clear();
        if (_tab == Passes)
        {
            _rows.Clear(x, w);
            PassesTab(ui, x, top, w, y + h);
            return;
        }

        if (_tab == Inspector)
        {
            _rows.Clear(x, w);
            InspectorTab(ui, x, top, w, y + h);
            return;
        }

        _rows.Clear(x, w);
        if (_tab == Search)
        {
            top = SearchBox(ui, x, top, w);
            Searched();
        }
        else if (_tab == Problems)
        {
            ProblemLines();
        }

        Body(ui, x, top, w, y + h);
        host.Hits.Region(x, top, w, y + h - top, this);
    }

    private void Head(HudUi ui, double x, double y, double w, double h)
    {
        var at = x + ui.Px(16);
        if (docked)
        {
            _ = ui.Spaced(at, y, h, ui.F.Small, Caps(L(_tab == Search ? "search-tab" : "passes-tab")), ui.Px(2.5), HudUi.Soft);
            return;
        }

        foreach (var (i, key, click) in (_tabClicks ?? []).Bounded(3))
        {
            var caps = Caps(L(key));
            var tw = ui.Spaced(at, y, h, ui.F.Small, caps, ui.Px(2.5), i == _tab ? Rgba.White(1) : HudUi.Faint);
            if (i == Problems && Errors + Warnings() > 0)
            {
                var count = CountText(Errors + Warnings());
                ui.Text(at + tw + ui.Px(6), y, h, ui.F.Small, count, HudUi.Faint);
                tw += ui.TextW(ui.F.Small, count) + ui.Px(6);
            }

            if (i == _tab) ui.Fill(at, y + h - ui.Px(4), tw, Math.Max(1, ui.Px(1)), HudUi.Link);

            host.Hits.Add(at - ui.Px(6), y, tw + ui.Px(12), h, click);
            at += tw + ui.Px(22);
        }

        StudioIcons.Close(ui, x + w - ui.Px(20), y + h / 2, ui.Px(8), HudUi.Faint);
        host.Hits.Add(x + w - ui.Px(32), y, ui.Px(28), h, _close!);
    }

    private int Warnings()
    {
        var warnings = 0;
        foreach (var (_, issues) in _problems.Bounded(MaxFiles))
        {
            warnings += issues.Length - ShaderDiagnostics.Errors(issues);
        }

        return warnings;
    }

    // The query, its count, and below the replace field with "Replace all" (writes the project copies among the hits)
    private double SearchBox(HudUi ui, double x, double y, double w)
    {
        var fw = Math.Min(ui.Px(360), w - ui.Px(140));
        var searching = host.Focus == StudioFocus.Search;
        var h = _query.Draw(ui, x + ui.Px(16), y + ui.Px(4), fw, L("search-all"), searching && !_replacing);
        host.Hits.Add(x + ui.Px(16), y + ui.Px(4), fw, h, _focusSearch!);
        ui.Text(x + ui.Px(26) + fw, y + ui.Px(4), h, ui.F.Small, _countText, HudUi.Faint);
        y += h + ui.Px(10);
        h = _replace.Draw(ui, x + ui.Px(16), y, fw, L("replace-with"), searching && _replacing);
        host.Hits.Add(x + ui.Px(16), y, fw, h, _focusReplace!);
        var label = L("replace-all");
        var bw = ui.TextW(ui.F.Small, label) + ui.Px(16);
        var on = _hitCount > 0;
        ui.Fill(x + ui.Px(26) + fw, y, bw, h, HudUi.ChipBack, ui.Px(4));
        ui.Text(x + ui.Px(34) + fw, y, h, ui.F.Small, label, on ? HudUi.Soft : HudUi.Faint);
        if (on) host.Hits.Add(x + ui.Px(26) + fw, y, bw, h, _replaceAll!);
        return y + h + ui.Px(10);
    }

    private void ProblemLines()
    {
        foreach (var (path, issues) in _problems.Bounded(MaxFiles))
        {
            _lines.Add(new Line(path, 0, 0, Folder(path), null, issues.Length, true, 0));
            foreach (var issue in issues.Bounded(128))
            {
                _lines.Add(new Line(path, issue.Line, issue.Column, issue.Message, issue, 0, false, 0));
            }
        }
    }

    // The search's hits when the query changed: grouped by file, 30 a file
    private void Searched()
    {
        var q = _query.Text.Trim();
        if (_queryVersion != _query.Version)
        {
            _queryVersion = _query.Version;
            (_hitText, _hitCount) = (q, 0);
            _found.Clear();
            if (q.Length >= 2) Find(q);

            _countText = _hitCount > 0 ? L("hits", _hitCount) : "";
        }

        _lines.AddRange(_found);
    }

    private readonly List<Line> _found = [];

    private void Find(string q)
    {
        List<string> files = [.. Files(StudioLink.ProjectsFolder), .. ShaderAssets.Files(), .. InstalledShaders.Files(host.Api)];

        foreach (var file in files.Bounded(MaxSearchFiles))
        {
            string text;
            if (FindTab(file) is { } open)
            {
                text = open.Text;
            }
            else if (!StudioLink.Read(file, out text, out _))
            {
                continue;
            }

            FindIn(file, text, q);
        }
    }

    private void FindIn(string file, string text, string q)
    {
        var lines = text.Split('\n');
        var heading = _found.Count;
        var count = 0;
        for (var i = 0; i < Math.Min(lines.Length, ShaderPaths.MaxSource); i++)
        {
            var at = lines[i].IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            if (count == 0) _found.Add(new Line(file, 0, 0, Folder(file), null, 0, true, 0));

            count++;
            if (count <= MaxHits) _found.Add(new Line(file, i, at + 1, lines[i], null, 0, false, at));
        }

        if (count > 0)
        {
            _found[heading] = _found[heading] with { Count = count };
            _hitCount += count;
        }
    }

    private static string Folder(string path)
    {
        if (InstalledShaders.Folder(path) is { } mod) return mod;

        var folder = Path.GetDirectoryName(path) ?? "";
        var display = StudioLink.Display(folder);
        var projects = StudioLink.Display(StudioLink.ProjectsFolder) + "/";
        return (display.StartsWith(projects, StringComparison.Ordinal) ? display[projects.Length..] : display) + "/";
    }

    private void Body(HudUi ui, double x, double top, double w, double bottom)
    {
        var row = ui.Px(RowHeight);
        if (_tab == Output)
        {
            OutputLines(ui, x, top, w, bottom, row);
            return;
        }

        if (_lines.Count == 0)
        {
            var empty = Empty();
            ui.Text(x + ui.Px(16), top + ui.Px(6), row, ui.F.Body, empty, HudUi.Faint);
            return;
        }

        var shown = Math.Max(1, (int)((bottom - top) / row));
        _first = Math.Clamp(_first, 0, Math.Max(0, _lines.Count - shown));
        for (var i = 0; i < Math.Min(shown, 256) && _first + i < _lines.Count; i++)
        {
            var y = top + i * row;
            var line = _lines[_first + i];
            if (line.Heading)
            {
                FileRow(ui, x, y, w, row, line);
                continue;
            }

            _rows.Add(y, row, _first + i);
            if (host.Hot(x, y, w, row)) ui.Fill(x, y, w, row, HudUi.Track with { A = 0.045 });

            if (line.Issue is { } issue)
            {
                ProblemRow(ui, x, y, w, row, issue);
            }
            else
            {
                HitRow(ui, x, y, w, row, line);
            }
        }
    }

    private string Empty()
    {
        if (_tab == Problems) return L("no-problems-in", host.Project ?? L("project-word"));

        return _query.Text.Trim().Length < 2 ? L("two-letters") : L("no-hits-dot");
    }

    private static void FileRow(HudUi ui, double x, double y, double w, double h, Line line)
    {
        StudioIcons.File(ui, x + ui.Px(16), y + h / 2, ui.Px(14), Path.GetExtension(line.Path).ToLowerInvariant());
        var name = StudioLink.Name(line.Path);
        var at = x + ui.Px(38);
        ui.Text(at, y, h, ui.F.Body, name, HudUi.Soft);
        at += ui.TextW(ui.F.Body, name) + ui.Px(10);
        var folder = ui.Fit(ui.F.Small, line.Text, Math.Max(ui.Px(20), w * 0.5));
        ui.Text(at, y, h, ui.F.Small, folder, HudUi.Faint);
        at += ui.TextW(ui.F.Small, folder) + ui.Px(10);
        if (line.Count > 0)
        {
            var count = line.Count.ToString(CultureInfo.InvariantCulture);
            var cw = ui.TextW(ui.F.Small, count) + ui.Px(12);
            ui.Fill(at, y + ui.Px(4), cw, h - ui.Px(8), HudUi.ChipBack, (h - ui.Px(8)) / 2);
            ui.Text(at + ui.Px(6), y, h, ui.F.Small, count, HudUi.Soft);
        }
    }

    // ✕ or ! , the message, the renderer chip, [Ln, Sp]
    private static void ProblemRow(HudUi ui, double x, double y, double w, double h, Diagnostic issue)
    {
        var at = x + ui.Px(38);
        if (issue.Error)
        {
            StudioIcons.Close(ui, at + ui.Px(5), y + h / 2, ui.Px(8), HudUi.Err);
        }
        else
        {
            ui.Text(at + ui.Px(3), y, h, ui.F.Strong, "!", HudUi.Warn);
        }

        at += ui.Px(20);
        var where = "[" + L("ln") + " " + (issue.Line + 1).ToString(CultureInfo.InvariantCulture) + ", " + L("col") +
                    " " + issue.Column.ToString(CultureInfo.InvariantCulture) + "]";
        var vulkanOnly = issue.Renderer == "Vulkan";
        var chipW = vulkanOnly ? ui.TextW(ui.F.Small, "OpenGL  · Vulkan ") + ui.Px(44) : 0;
        var whereW = ui.TextW(ui.F.Small, where);
        var message = ui.Fit(ui.F.Body, issue.Message, Math.Max(ui.Px(40), x + w - at - chipW - whereW - ui.Px(40)));
        ui.Text(at, y, h, ui.F.Body, message, HudUi.Soft);
        at += ui.TextW(ui.F.Body, message) + ui.Px(10);
        if (vulkanOnly)
        {
            ui.Fill(at, y + ui.Px(4), chipW, h - ui.Px(8), HudUi.Err with { A = 0.1 });
            Chip(ui, at + ui.Px(7), y, h);
            at += chipW + ui.Px(10);
        }

        ui.Text(at, y, h, ui.F.Small, where, HudUi.Faint);
    }

    // OpenGL ✓ · Vulkan ✕, the marks drawn (the game's fonts lack them)
    private static void Chip(HudUi ui, double x, double y, double h)
    {
        ui.Text(x, y, h, ui.F.Small, "OpenGL", HudUi.Soft);
        x += ui.TextW(ui.F.Small, "OpenGL") + ui.Px(4);
        x += ui.Check(x, y, h, HudUi.Ok) + ui.Px(4);
        ui.Text(x, y, h, ui.F.Small, "· Vulkan", HudUi.Soft);
        x += ui.TextW(ui.F.Small, "· Vulkan") + ui.Px(5);
        ui.Cross(x, y + h / 2 - ui.Px(4), ui.Px(8), HudUi.Err);
    }

    private void HitRow(HudUi ui, double x, double y, double w, double h, Line line)
    {
        var number = (line.Row + 1).ToString(CultureInfo.InvariantCulture);
        ui.Text(x + ui.Px(52) - ui.TextW(ui.F.Mono, number), y, h, ui.F.Mono, number, HudUi.Faint);
        var text = line.Text.TrimStart();
        var lead = line.Text.Length - text.Length;
        var at = Math.Clamp(line.At - lead, 0, text.Length);
        var end = Math.Min(text.Length, at + _hitText.Length);
        var left = x + ui.Px(62);
        var before = CodeText.Display(text[..at]);
        var mark = text[at..end];
        ui.Text(left, y, h, ui.F.Mono, ui.Fit(ui.F.Mono, before, w - ui.Px(80)), HudUi.Soft);
        var mx = left + ui.TextW(ui.F.Mono, before);
        if (mx < x + w - ui.Px(20))
        {
            ui.Fill(mx, y + ui.Px(3), ui.TextW(ui.F.Mono, mark), h - ui.Px(6), StudioColors.Gold with { A = 0.3 });
            ui.Text(mx, y, h, ui.F.Mono, ui.Fit(ui.F.Mono, CodeText.Display(text[at..]), x + w - mx - ui.Px(20)),
                Rgba.White(1));
        }
    }

    private void OutputLines(HudUi ui, double x, double top, double w, double bottom, double row)
    {
        var log = host.Log;
        if (log.Count == 0)
        {
            ui.Text(x + ui.Px(16), top + ui.Px(6), row, ui.F.Body, L("no-output"), HudUi.Faint);
            return;
        }

        var shown = Math.Max(1, (int)((bottom - top) / row));
        _first = Math.Clamp(_first, 0, Math.Max(0, log.Count - shown));
        for (var i = 0; i < Math.Min(shown, 128) && _first + i < log.Count; i++)
        {
            var (time, text, error) = log[_first + i];
            var y = top + i * row;
            ui.Text(x + ui.Px(16), y, row, ui.F.Mono, time, HudUi.Faint);
            ui.Text(x + ui.Px(90), y, row, ui.F.Body, ui.Fit(ui.F.Body, text, w - ui.Px(110)),
                error ? HudUi.Err : HudUi.Soft);
        }
    }
}
