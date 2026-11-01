using System.Collections.Concurrent;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// A chunk's key in the band tables: y above z above x, 21 bits each; -1 for a position NowProcessChunk turns away
internal static class ChunkKey
{
    private const int Size = 32, Bits = 21, Mask = (1 << Bits) - 1;

    public static long Pack(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || !Assert(x <= Mask && z <= Mask) || !Assert(y <= Mask)) return -1;
        return ((long)y << 2 * Bits) | ((long)z << Bits) | (uint)x;
    }

    public static (int X, int Y, int Z) Unpack(long key) =>
        Assert(key >= 0) && Assert(key >> 2 * Bits <= Mask)
            ? ((int)(key & Mask), (int)(key >> 2 * Bits), (int)((key >> Bits) & Mask))
            : (-1, -1, -1);

    // Horizontal distance in blocks from (x, z) to the chunk's centre column
    public static double CentreDistance(int cx, int cz, double x, double z)
    {
        double dx = (double)cx * Size + Size / 2.0 - x, dz = (double)cz * Size + Size / 2.0 - z;
        return Assert(double.IsFinite(dx)) && Assert(double.IsFinite(dz)) ? Math.Sqrt(dx * dx + dz * dz) : double.MaxValue;
    }
}

// Near and far bands by the size a block covers on screen (FarLod, FarFoliage)
internal static class PixelBand
{
    public const int Near = 1, Far = 2;
    private const double HalfDiagonal = 23, Margin = 32;

    // Distance in blocks where a block covers `pixels` pixels: screen height / (tangent span * pixels); NaN while off
    public static double Distance(double height, double fovDegrees, int pixels, double minDistance)
    {
        if (!Assert(pixels >= 0) || !Finite(minDistance) || pixels == 0 || double.IsNaN(height) || height <= 0 ||
            !(fovDegrees is > 1 and < 179)) return double.NaN;
        var span = 2 * Math.Tan(fovDegrees * Math.PI / 360);
        return Math.Max(minDistance, height / span / pixels);
    }

    // Band wanted at centre distance d given the current band (0: none). The near band reaches past the threshold by a
    // chunk's half diagonal and Margin; a chunk turns far only `width` blocks beyond that, and near again inside it.
    public static int Want(double d, int band, double threshold, double width)
    {
        if (!double.IsFinite(threshold) || !Assert(band is >= 0 and <= Far) || !Assert(width > 0)) return Near;
        var (near, far) = (threshold + HalfDiagonal + Margin, threshold + HalfDiagonal + Margin + width);
        return band switch
        {
            Far => d < near ? Near : Far,
            Near => d > far ? Far : Near,
            _ => d > (near + far) / 2 ? Far : Near
        };
    }
}

// The chunks a distance feature (FarLod, FarFoliage, DeepCulling) tessellated, each with the band it was drawn in:
// Near or Far, FarLod's farthest chunks Farthest (DeepCulling: full or culled). Tessellation threads write a chunk's band at its pass; the main thread
// walks the table in rounds, a slice per scan, and marks the chunks whose band no longer fits. A round starts only once
// the last is done and the player moved Moved blocks, the band distance changed or RoundMs passed, so a player standing
// still costs next to nothing. A marked chunk waits PendingMs for its pass before it is marked again. Every SweepMs a
// round drops the chunks the map no longer holds, whatever their band.
internal sealed class ChunkBands
{
    public const byte Near = PixelBand.Near, Far = PixelBand.Far, Farthest = 3;
    private const int MaxTracked = 1 << 22, PendingMs = 20_000, RoundMs = 5000, SweepMs = 30_000;
    private const double Moved = 8;

    // Key -> band; written by the tessellation threads and the main thread
    public readonly ConcurrentDictionary<long, byte> Bands = new();

    // Key -> when it was marked, until its pass begins
    public readonly ConcurrentDictionary<long, long> Pending = new();

    // Key -> the band drawn, while Bands holds a different one announced at a mark (FarLod) and its pass has not come
    public readonly ConcurrentDictionary<long, byte> Drawn = new();

    // The main thread's round: Bands' keys as it began, walked from the cursor
    private long[] _round = [];
    private int _count, _cursor, _far, _published, _farthest, _farthestPublished;
    private long _began, _swept;
    private double _x, _z, _setting;
    private bool _sweeping;

    // Of the chunks the last round walked, those in the far band (DeepCulling: culled); 0 once nothing is tracked,
    // as the scans stop starting rounds then
    public int FarChunks => Bands.IsEmpty ? 0 : _published;

    // Of those, the ones in FarLod's Farthest band
    public int FarthestChunks => Bands.IsEmpty ? 0 : _farthestPublished;

    // A new round when the last one is done and the player moved, the setting (threshold or near distance, MaxValue
    // while off) changed by a block or RoundMs passed; the keys are copied (a copy only, the tests come in slices)
    public void Begin(double x, double z, double setting, long now)
    {
        if (_cursor < _count) return;
        var changed = Math.Abs(x - _x) >= Moved || Math.Abs(z - _z) >= Moved || Math.Abs(setting - _setting) >= 1;
        if (_began != 0 && !changed && now - _began < RoundMs) return;
        (_published, _far, _farthestPublished, _farthest, _cursor, _count) = (_far, 0, _farthest, 0, 0, 0);
        (_x, _z, _setting, _began) = (x, z, setting, now);
        _sweeping = now - _swept >= SweepMs;
        if (_sweeping) _swept = now;
        var tracked = Math.Min(Bands.Count, MaxTracked);
        if (_round.Length < tracked) _round = new long[Math.Max(tracked + 1024, _round.Length * 2)];
        foreach (var (key, _) in Bands.Bounded(MaxTracked))
        {
            if (_count >= _round.Length) break; // grew meanwhile: the rest next round
            _round[_count++] = key;
        }
    }

    // The round's next key, false once the round is done. band 0: nothing to decide (gone since the round began,
    // waiting for its pass, or swept as the map no longer holds it)
    public bool TryNext(ClientMain game, long now, out long key, out byte band)
    {
        (key, band) = (-1, 0);
        if (_cursor >= _count || !NotNull(game)) return false;
        key = _round[_cursor++];
        if (!Bands.TryGetValue(key, out var held)) return true;
        var (x, y, z) = ChunkKey.Unpack(key);
        if (_sweeping && game.BlockAccessor.GetChunk(x, y, z) is null)
        {
            Forget(key);
            return true;
        }

        _far += held >= Far ? 1 : 0;
        _farthest += held == Farthest ? 1 : 0;
        if (Waiting(key, now)) return true;
        band = Drawn.TryGetValue(key, out var drawn) ? drawn : held; // a mark whose pass never came decides anew
        return Assert(band is >= Near and <= Farthest);
    }

    // Bands takes the band a chunk is about to be drawn in, so neighbours tessellated first already face it; Drawn
    // keeps the band its geometry still has until its pass begins
    public void Announce(long key, byte band)
    {
        if (!Assert(band is >= Near and <= Farthest) || !Bands.TryGetValue(key, out var held)) return;
        var drawn = Drawn.TryGetValue(key, out var was) ? was : held;
        if (drawn == band) _ = Drawn.TryRemove(key, out _);
        else Drawn[key] = drawn;
        Bands[key] = band;
    }

    // The band a chunk's geometry has, 0 when untracked
    public byte DrawnBand(long key)
    {
        if (Drawn.TryGetValue(key, out var drawn)) return drawn;
        return Bands.TryGetValue(key, out var held) ? held : (byte)0;
    }

    // Marked for a pass, unless one is pending or the map lost the chunk (then it is forgotten): 1 when marked
    public int Mark(ClientMain game, int x, int y, int z, long now)
    {
        var key = ChunkKey.Pack(x, y, z);
        if (key < 0 || !NotNull(game) || Waiting(key, now)) return 0; // Waiting drops a stale mark
        if (game.BlockAccessor.GetChunk(x, y, z) is null)
        {
            Forget(key);
            return 0;
        }

        Pending[key] = now;
        game.WorldMap.MarkChunkDirty(x, y, z, fireEvent: false);
        return 1;
    }

    // Marked less than PendingMs ago; an older mark whose pass never came is dropped
    public bool Waiting(long key, long now)
    {
        if (!Pending.TryGetValue(key, out var since)) return false;
        if (now - since < PendingMs) return true;
        _ = Pending.TryRemove(key, out _);
        return false;
    }

    public void Forget(long key)
    {
        _ = Bands.TryRemove(key, out _);
        _ = Pending.TryRemove(key, out _);
        _ = Drawn.TryRemove(key, out _);
    }

    public void Clear()
    {
        Bands.Clear();
        Pending.Clear();
        Drawn.Clear();
        (_count, _cursor, _far, _published, _began, _swept, _sweeping) = (0, 0, 0, 0, 0, 0, false);
        (_farthest, _farthestPublished, _round) = (0, 0, []); // the next world's first round published the last one's
        (_x, _z, _setting) = (0, 0, 0);
    }
}
