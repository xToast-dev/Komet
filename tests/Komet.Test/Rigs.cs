using Komet.Core;
using Komet.Diagnostics;
using Komet.Api;
using Komet.Tessellation;
using Vintagestory.API.Client.Tesselation;

namespace Komet.Test.Rigs;

// Stamped by Komet.Test.csproj
internal static class Paths
{
    public static readonly string Repo = Stamped("KometRepo");
    public static readonly string KometDir = Path.Combine(Repo, "Komet");

    private static string Stamped(string key) =>
        typeof(Paths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;
}

// The game's English, loaded as the client loads it; Komet's own keys then translate to themselves
internal static class GameLang
{
    // Lang.Get throws until a language is loaded: the game's English, unless one is loaded already
    public static void EnsureLoaded()
    {
        if (Lang.CurrentLocale is not { } locale || !Lang.AvailableLanguages.ContainsKey(locale)) LoadEnglish();
    }

    public static void LoadEnglish()
    {
        var english = new TranslationService("en", new QuietLogger());
        english.PreLoad(GameInstall.Assets);
        Lang.AvailableLanguages["en"] = english;
        Lang.ChangeLanguage("en");
    }

    // Komet's English as the only language, its keys under komet:; the directory it wrote, for the caller to delete
    public static string LoadKomet()
    {
        var assets = Directory.CreateTempSubdirectory("komet-test-lang").FullName;
        using var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Join(Paths.KometDir, "assets", "komet", "lang", "en.json")));
        var entries = json.RootElement.EnumerateObject().ToDictionary(p => "komet:" + p.Name, p => p.Value.GetString());
        _ = Directory.CreateDirectory(Path.Join(assets, "game", "lang"));
        File.WriteAllText(Path.Join(assets, "game", "lang", "en.json"), System.Text.Json.JsonSerializer.Serialize(entries));
        var english = new TranslationService("en", new QuietLogger());
        english.PreLoad(assets);
        Lang.AvailableLanguages["en"] = english;
        Lang.ChangeLanguage("en");
        return assets;
    }
}

// Komet's own features in install order
internal static class BuiltIns
{
    public static Feature[] All() => [.. Enumerable.Range(0, FeatureTable.BuiltInCount).Select(FeatureTable.At)];
}

// One built-in feature installed through its registry entry, as KometModSystem installs it: a server in this process and no game API,
// which none of the features the tests install asks for.
internal sealed class FeatureRig : IDisposable
{
    private readonly TestHarmony _harmony;

    public FeatureRig(string id, ILogger? logger = null)
    {
        Feature = Features.Find(id);
        Assert.That(Feature, Is.GreaterThanOrEqualTo(0), id);
        _harmony = new TestHarmony("komet-test-feature-" + id.ToLowerInvariant());
        FeatureLifecycle.InstallAt(new FeatureContext(_harmony, null!, logger ?? new QuietLogger(), true), Feature);
    }

    public int Feature { get; }

    public KapiFeatureState State => Features.Evaluate(Feature);

    public void Dispose()
    {
        FeatureTable.At(Feature).Stop?.Invoke(null);
        _harmony.Dispose();
        FeatureLifecycle.Close();
    }
}

// A ChunkTesselator without a game: only the arrays CalculateVisibleFaces and CalcBlockFaceLight read - the 34^3 halo of solid and
// fluid blocks and light, the draw buffer, blocksFast and tmpPos - and TileSideEnum.MoveIndex as ChunkTesselator.Start sets it.
// Everything else stays null, which the engine's paths under test never touch.
internal sealed class TessRig : IDisposable
{
    public const int Ext = TessSeams.Ext,
        ExtCells = TessSeams.ExtCells,
        Plane = TessSeams.Plane;

    private const int Cells = TessSeams.Size * TessSeams.Size * TessSeams.Size;

    public static readonly int[] Moves = TessSeams.Moves;

    private readonly int[] _moves;

    public TessRig(Block[] palette)
    {
        _moves = [.. TileSideEnum.MoveIndex];
        Moves.CopyTo(TileSideEnum.MoveIndex, 0);
        Tesselator = (ChunkTesselator)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselator));
        Solid = TessSeams.BlocksExt(Tesselator) = new Block[ExtCells];
        Fluid = TessSeams.FluidsExt(Tesselator) = new Block[ExtCells];
        Rgb = TessSeams.RgbsExt(Tesselator) = new int[ExtCells];
        TessSeams.Draw(Tesselator) = new byte[Cells];
        TessSeams.BlocksFast(Tesselator) = palette;
        _ = TessSeams.TmpPos(Tesselator) = new BlockPos(0);
        Vars = new TCTCache(Tesselator);
    }

    private ChunkTesselator Tesselator { get; }
    public TCTCache Vars { get; }
    public Block[] Solid { get; }
    public Block[] Fluid { get; }
    public int[] Rgb { get; }

    public void Dispose() => _moves.CopyTo(TileSideEnum.MoveIndex, 0);

    // The engine's internal methods, called through their entry: patched when a test patched them
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CalcBlockFaceLight")]
    public static extern long CalcBlockFaceLight(TCTCache vars, int tileSide, int extNeibIndex3D);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "neighbourLightRGBS")]
    public static extern ref int[] Neighbours(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jsonLightRGB")]
    public static extern ref int[] JsonLight(JsonTesselator tesselator);
}

// Deterministic answers for the recording blocks: the same call gets the same answer in both runs
internal static class TessMix
{
    public static int Hash(int x, int y, int z)
    {
        var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791);
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        return (int)((h ^ (h >> 15)) & 0x7FFFFFFF);
    }

    public static bool Bit(params int[] values)
    {
        var h = 0x9E3779B9u;
        foreach (var v in values)
        {
            h = ((h ^ (uint)v) * 0x85EBCA6Bu) ^ (h >> 13);
        }

        return ((h ^ (h >> 16)) & 1) != 0;
    }
}

// IModLoader with one mod; only Mods is read by the walk
public class OneModLoader : DispatchProxy
{
    protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "get_Mods") throw new NotSupportedException(targetMethod?.Name);

        var mod = new TestMod();
        typeof(Mod).GetProperty(nameof(Mod.Info))!.SetValue(mod,
            new ModInfo { Name = "Komet", Version = "1.2.3", ModID = "komet" });
        return new Mod[] { mod };
    }

    private sealed class TestMod : Mod;
}

// FrameClock's switches and the engine's profiler as a test sets them, put back afterwards: no sink, a fresh frame start
internal static class Clock
{
    public static void With(bool stats, FrameProfilerUtil? profiler, Action body)
    {
        var saved = (FrameClock.Stats, ScreenManager.FrameProfiler);
        (FrameClock.Stats, ScreenManager.FrameProfiler) = (stats, profiler!);
        try
        {
            body();
        }
        finally
        {
            (FrameClock.Stats, ScreenManager.FrameProfiler, FrameClock.Sink) = (saved.Stats, saved.FrameProfiler, null);
            FrameClock.Begin();
        }
    }
}
