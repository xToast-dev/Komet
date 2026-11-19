using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// The shader test (".komet shadertest", or KOMET_SHADER_TEST=<folder> at the world's start): every installed shader and
// include opens in turn in the enlarged 3D view, its picture is saved, judged (an error, empty, black, flat) and put on
// contact sheets; report.txt lists them. Written for an AI to look at the sheets and say what looks wrong. A file
// "done" marks the end; with exit the game closes then (the sandbox run of scripts/shadertest.sh).
internal sealed partial class ShaderIde
{
    private const long SettleMs = 2500, CaptureMs = 2000;
    private const int Thumb = 4, SheetColumns = 4, SheetRows = 5, MaxPixels = 1 << 24, MaxFiles = 4096, MaxSide = 4096;

    private ShaderTest? _test;

    public bool Testing => _test is not null;

    // Every vanilla stage and include; a vertex stage only where no fragment stage of the same name draws the picture
    public void StartTest(string folder, bool exit)
    {
        var all = ShaderAssets.Files();
        var files = all.Where(f => !f.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase) ||
                                   !File.Exists(Path.ChangeExtension(f, ".fsh"))).ToArray();
        _ = Directory.CreateDirectory(folder);
        _test = new ShaderTest(folder, files, exit);
        AddLog("shader test: " + files.Length + " files → " + folder);
    }

    // Per poll: open the next file, let the view settle, ask the render thread for its picture, judge it
    private bool TestPoll(long now)
    {
        if (_test is not { } test) return false;

        if (test.At < 0 || ((test.Shot is not null || now - test.Since > SettleMs + CaptureMs) && !Next(test, now)))
        {
            if (test.At < 0) return Next(test, now);

            FinishTest(test);
            return true;
        }

        if (test is { Want: false, Shot: null } && now - test.Since > SettleMs) test.Want = true;

        return true;
    }

    // The current file judged and kept, the next one opened; false when there is none
    private bool Next(ShaderTest test, long now)
    {
        if (test.At >= 0) Judge(test);

        test.At++;
        (test.Shot, test.Want, test.Since) = (null, false, now);
        if (test.At >= test.Files.Length) return false;

        OpenExact(test.Files[test.At]);
        Mode = EditorMode.Mine;
        if (!Model3D.On) ToggleModel();

        Model3D.ResetObject();
        Model3D.Shape = PreviewProgram is { } program ? AutoShape(program) : 0;
        Buffers.Large = true;
        _modelStamp = -1;
        return true;
    }

    // Render thread: the picture the view just drew, when the test asks for one
    private void TestCapture()
    {
        if (_test is not { Want: true } test) return;

        test.Shot = Model3D.Read();
        test.Want = false;
        if (test.Shot is not { } shot) return;

        // as the screen shows it: the preview draws opaque, an alpha below one only says how the game would blend
        for (var i = 3; i < Math.Min(shot.Rgba.Length, MaxPixels * 4); i += 4) shot.Rgba[i] = 255;
    }

    private void Judge(ShaderTest test)
    {
        var file = test.Files[test.At];
        var name = StudioLink.Relative(file) ?? Path.GetFileName(file);
        var index = test.Pictures.Count;
        var note = Model3D.Note;
        var verdict = test.Shot is { } shot ? Verdict(shot) : "NO PICTURE";
        if (note.Length > 0) verdict = "ERROR " + note;
        if (test.Shot is { } picture)
        {
            PngFile.Write(Path.Join(test.Folder, index.ToString("000", CultureInfo.InvariantCulture) + "-" +
                                                 Path.GetFileName(file) + ".png"), picture.Rgba, picture.Width,
                picture.Height);
        }

        test.Pictures.Add(test.Shot);
        var sheet = index / (SheetColumns * SheetRows);
        var cell = index % (SheetColumns * SheetRows);
        test.Lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"{index:000}  sheet {sheet} row {cell / SheetColumns} col {cell % SheetColumns}  {name}  [{PreviewProgram ?? "-"}]  {verdict}  draw {Model3D.LastMs:0.00} ms"));
    }

    // What the picture is: the view's empty ground, black, one flat colour, or "ok" with its mean and spread
    private static string Verdict((byte[] Rgba, int Width, int Height) shot)
    {
        var (sum, squares, black, ground, count) = (0.0, 0.0, 0, 0, shot.Width * shot.Height);
        for (var i = 0; i < Math.Min(count, MaxPixels); i++)
        {
            var (r, g, b) = (shot.Rgba[i * 4], shot.Rgba[i * 4 + 1], shot.Rgba[i * 4 + 2]);
            var luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
            (sum, squares) = (sum + luma, squares + luma * luma);
            black += r + g + b < 12 ? 1 : 0;
            ground += Math.Abs(r - 19) + Math.Abs(g - 22) + Math.Abs(b - 28) < 9 ? 1 : 0; // the view's clear colour
        }

        var mean = sum / count;
        var spread = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
        var stats = string.Create(CultureInfo.InvariantCulture, $"mean {mean:0.00} spread {spread:0.000}");
        if (ground > count * 0.98) return "EMPTY " + stats;
        if (black > count * 0.9) return "BLACK " + stats;

        return spread < 0.01 ? "FLAT " + stats : "ok " + stats;
    }

    private void FinishTest(ShaderTest test)
    {
        _test = null;
        Sheets(test);
        var report = new StringBuilder();
        _ = report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Komet shader test {DateTime.Now:yyyy-MM-dd HH:mm}, {test.Lines.Count} files, sheets of {SheetColumns}x{SheetRows}"));
        foreach (var line in test.Lines.Bounded(MaxFiles)) _ = report.AppendLine(line);

        _ = report.AppendLine(ReloadTimes());

        File.WriteAllText(Path.Join(test.Folder, "report.txt"), report.ToString());
        File.WriteAllText(Path.Join(test.Folder, "done"), "");
        AddLog("shader test done: " + test.Folder);
        if (test.Exit)
        {
            ScreenManager.EnqueueMainThreadTask(() =>
                ScreenManager.Platform.WindowExit("komet shader test done", EnumExitMode.SoftExit));
        }
    }

    // What a shader build costs in the game: the asset reload, the engine's programs, the whole reload with the mods'
    // listeners; each the best of three
    private string ReloadTimes()
    {
        var (assets, engine, whole) = (double.MaxValue, double.MaxValue, double.MaxValue);
        for (var i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            _ = ScreenManager.Platform.AssetManager.Reload(AssetCategory.shaders);
            _ = ScreenManager.Platform.AssetManager.Reload(AssetCategory.shaderincludes);
            assets = Math.Min(assets, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            _ = ShaderRegistry.ReloadShaders();
            engine = Math.Min(engine, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            _ = Api.Shader.ReloadShaders();
            whole = Math.Min(whole, clock.Elapsed.TotalMilliseconds);
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"reload: assets {assets:0} ms, engine programs (with assets) {engine:0} ms, whole with listeners {whole:0} ms");
    }

    // Contact sheets: the pictures at a quarter size, SheetColumns x SheetRows a sheet, a dark line between them
    private static void Sheets(ShaderTest test)
    {
        const int per = SheetColumns * SheetRows;
        var (tw, th) = (ModelView.Width / Thumb, 0);
        foreach (var picture in test.Pictures.Bounded(MaxFiles)) th = Math.Max(th, (picture?.Height ?? 0) / Thumb);

        if (th == 0) return;

        for (var sheet = 0; sheet < Math.Min((test.Pictures.Count + per - 1) / per, MaxFiles); sheet++)
        {
            var (w, h) = (SheetColumns * (tw + 2), SheetRows * (th + 2));
            var pixels = new byte[w * h * 4];
            for (var i = 0; i < Math.Min(pixels.Length, MaxPixels * 4); i += 4)
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (60, 0, 60, 255);

            for (var cell = 0; cell < per && sheet * per + cell < test.Pictures.Count; cell++)
            {
                if (test.Pictures[sheet * per + cell] is { } p)
                    Place(pixels, w, p, cell % SheetColumns * (tw + 2) + 1, cell / SheetColumns * (th + 2) + 1, tw, th);
            }

            PngFile.Write(Path.Join(test.Folder, string.Create(CultureInfo.InvariantCulture, $"sheet-{sheet}.png")),
                pixels, w, h);
        }
    }

    private static void Place(byte[] to, int width, (byte[] Rgba, int Width, int Height) from, int x, int y, int tw,
        int th)
    {
        for (var row = 0; row < Math.Min(Math.Min(th, from.Height / Thumb), MaxSide); row++)
        {
            for (var col = 0; col < Math.Min(tw, MaxSide); col++)
            {
                var source = (row * Thumb * from.Width + col * Thumb) * 4;
                var target = ((y + row) * width + x + col) * 4;
                Array.Copy(from.Rgba, source, to, target, 4);
            }
        }
    }

    private sealed class ShaderTest(string folder, string[] files, bool exit)
    {
        public string Folder { get; } = folder;
        public string[] Files { get; } = files;
        public bool Exit { get; } = exit;
        public int At { get; set; } = -1;
        public long Since { get; set; }
        public bool Want { get; set; }
        public (byte[] Rgba, int Width, int Height)? Shot { get; set; }
        public List<string> Lines { get; } = [];
        public List<(byte[] Rgba, int Width, int Height)?> Pictures { get; } = [];
    }
}
