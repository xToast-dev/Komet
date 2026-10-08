using System.Reflection;
using HarmonyLib;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Vintagestory.API.MathTools;

namespace Komet.World;

// The world map (essentials mod) stores every map piece it draws, a 32x32 colour image, in its own database: MapDB.SetMapPieces
// serializes each through protobuf-net into a growing MemoryStream, copies it out and executes the prepared INSERT through
// Microsoft.Data.Sqlite, which boxes and enumerates per row. A piece is drawn again whenever a neighbour arrives, so while chunks
// stream in that is a thousand pieces and 40 MB of garbage a second on the map thread, the largest single source of the collections
// that stop the render thread. The prefix writes the same rows in the same transaction through a raw statement, the bytes protobuf-net
// writes for MapPieceDB (field 1, one varint per pixel, sign-extended to ten bytes as protobuf-net does) encoded into the thread's
// buffer, which SQLite copies as it binds.
internal static class MapSaveScratch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xF8710161ED68084EUL;

    private const string Db = "Vintagestory.GameContent.MapDB", Piece = "Vintagestory.GameContent.MapPieceDB";
    private const string Insert = "INSERT OR REPLACE INTO mappiece (position, data) VALUES (@pos, @data)";
    private const byte Tag = 1 << 3; // field 1, varint
    private const int MaxVarint = 10, MaxPieces = 1 << 20, MaxPixels = 1 << 16;

    [ThreadStatic] private static byte[]? _buffer;
    private static MethodBase?[] _seams = [];
    private static Action<object, sqlite3>? _write;
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // Body not verified, essentials mod missing or another mod patches it: the engine saves
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _seams, _logger, _foreign, _write) = (false, true, Seams(), logger, false, null);
        if (!NotNull(harmony) || !Assert(_seams.Length == 1) || _seams[0] is null ||
            AccessTools.TypeByName(Piece) is not { } piece) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(MapSaveScratch), logger);
        if (!_shaped) return;
        _write = AccessTools.Method(typeof(MapSaveScratch), nameof(Write)).MakeGenericMethod(piece)
            .CreateDelegate<Action<object, sqlite3>>();
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Save)));
        Recheck();
    }

    // KometModSystem asks again on LevelFinalize, when every other mod has patched
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == 1);
        _foreign = EngineShape.Report(_logger, nameof(MapSaveScratch), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.Replacing, null, typeof(MapSaveScratch)));
        Blocked = !seamed || _foreign;
    }

    internal static MethodBase?[] Seams()
    {
        var (db, piece) = (AccessTools.TypeByName(Db), AccessTools.TypeByName(Piece));
        MethodBase?[] seams =
        [
            db is null || piece is null
                ? null
                : AccessTools.DeclaredMethod(db, "SetMapPieces",
                    [typeof(Dictionary<,>).MakeGenericType(typeof(FastVec2i), piece)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Save(object pieces, SqliteConnection? ___sqliteConn)
    {
        if (!Enabled || Blocked || _write is null || pieces is null || ___sqliteConn?.Handle is not { } db) return true;
        using var transaction = ___sqliteConn.BeginTransaction();
        _write(pieces, db);
        transaction.Commit();
        return false;
    }

    private static void Write<T>(object pieces, sqlite3 db) where T : class
    {
        SqliteException.ThrowExceptionForRC(raw.sqlite3_prepare_v2(db, Insert, out var statement), db);
        var rows = (Dictionary<FastVec2i, T>)pieces;
        _ = Assert(rows.Count <= MaxPieces);
        using var each = rows.GetEnumerator();
        using (statement)
            for (var i = 0; i < MaxPieces && each.MoveNext(); i++)
            {
                var (key, piece) = each.Current;
                var pixels = MapPixels.Pixels<T>.Of(piece);
                var length = Encode(pixels, ref _buffer);
                MapPixels.Release(pixels); // encoded: the save is done with it
                SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_int64(statement, 1, (long)key.ToChunkIndex()), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_blob(statement, 2, _buffer.AsSpan(0, length)), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_step(statement), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_reset(statement), db);
            }
    }

    // MapPieceDB as protobuf-net writes it: nothing for no pixels, else each pixel tagged, a varint of its 64-bit sign extension
    internal static int Encode(int[]? pixels, ref byte[]? buffer)
    {
        if (pixels is null || !Assert(pixels.Length <= MaxPixels)) return 0;
        var need = pixels.Length * (1 + MaxVarint);
        if (buffer is null || buffer.Length < need) buffer = new byte[need];
        var at = 0;
        for (var i = 0; i < Math.Min(pixels.Length, MaxPixels); i++)
        {
            buffer[at++] = Tag;
            var value = (ulong)(long)pixels[i];
            for (var b = 1; b < MaxVarint && value >= 0x80; b++, value >>= 7) buffer[at++] = (byte)(value | 0x80);
            buffer[at++] = (byte)value;
        }

        return at;
    }
}
