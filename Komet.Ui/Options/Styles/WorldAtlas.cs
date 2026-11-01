using Microsoft.Data.Sqlite;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Komet.Options;

// The world map the player has explored, as a globe sees it: the game's own map pieces (Maps/<world>.db, one colour per
// block column, as the world map and minimap draw them), read on a worker through a read-only connection of its own
// and laid into a square around the player that reaches as far as most of the explored land does; the globe turns to
// the player's place, a jump (a teleport) as a turn of a second or so. Explored texels are opaque,
// the rest clear. No map (switched off by the server, no map mod system): no atlas, and a style draws no planet.
internal sealed class WorldAtlas(ICoreClientAPI capi) : IDisposable
{
    public const int Size = 256;
    private const int Count = Size * Size, Chunk = 32, MinReach = 512, MaxReach = 1 << 17, MaxPieces = 1 << 18, MaxBlob = 1 << 16;
    private const double Refresh = 30, Moved = 512, Turn = 2.5; // Turn: the view's ease per second

    private LoadedTexture _texture = new(capi);
    private Task<(int[]? Pixels, double Reach, (double X, double Z) Center)>? _job;
    private (double X, double Z, double At) _made = (double.NaN, double.NaN, double.NegativeInfinity);
    private bool _warned;
    private (double X, double Z) _view = (double.NaN, double.NaN); // where the globe looks: eased toward the player
    private long _viewAt;
    private WorldMapManager? _maps; // found once: GetModSystem is a LINQ walk over every mod system, asked several times a frame

    public int Texture => Allowed ? _texture.TextureId : 0;

    // Whether this world has a map the player may see
    public bool Allowed => capi.World is not null && Maps() is { } maps && maps.mapAllowedClient();

    // The atlas lives as long as its world (OptionsScreen per client API), so the mod system it found stays that world's
    private WorldMapManager? Maps()
    {
        _maps ??= capi.ModLoader?.GetModSystem<WorldMapManager>();
        return _maps;
    }

    // Blocks from the atlas' centre to its edge, and where its centre lies in the world (the player's place when read)
    public double Reach { get; private set; } = MinReach;
    public (double X, double Z) Center { get; private set; }

    // The globe at the world's true size: its radius in blocks (the map's width as the circumference), the map's reach,
    // the player's offset from the atlas' centre, the centre's place inside its chunk (to lay the chunk grid), the
    // player's yaw (radians, the game's: looking toward -sin, -cos in x, z), the centre's chunk
    public (float Radius, float Reach, float Dx, float Dz, float Cx, float Cz, float Yaw, float ChunkX, float ChunkZ) Scale()
    {
        var at = capi.World?.Player?.Entity?.Pos;
        var width = capi.World?.BlockAccessor?.MapSizeX ?? 1024000;
        if (at is null || !Assert(width > 0)) return default;
        var (x, z) = Viewed(at.X, at.Z);
        return ((float)(width / (2 * Math.PI)), (float)Reach, (float)(x - Center.X), (float)(z - Center.Z),
            (float)(((Center.X % Chunk) + Chunk) % Chunk), (float)(((Center.Z % Chunk) + Chunk) % Chunk), at.Yaw,
            (float)Math.Floor(Center.X / Chunk), (float)Math.Floor(Center.Z / Chunk));
    }

    // The place the globe looks at, eased toward the player's: a teleport turns the globe instead of jumping
    private (double X, double Z) Viewed(double x, double z)
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _viewAt) / 1000.0, 0, 0.25); // a closed screen's pause does not skip the turn
        _viewAt = now;
        if (!double.IsFinite(_view.X + _view.Z)) _view = (x, z); // the first look: no turn from nowhere
        var k = 1 - Math.Exp(-dt * Turn);
        _view = (_view.X + (x - _view.X) * k, _view.Z + (z - _view.Z) * k);
        return _view;
    }

    // A new atlas after a while or once the player moved far; a finished one uploaded
    public void Update(double clock)
    {
        if (_job is { IsCompleted: true } done)
        {
            _job = null;
            if (done.IsCompletedSuccessfully && done.Result.Pixels is { } pixels)
            {
                Upload(pixels);
                (Reach, Center) = (done.Result.Reach, done.Result.Center); // with the texture they describe
            }
            else Warn(done.Exception?.GetBaseException()?.Message);
        }

        if (_job is not null || !Allowed || capi.World?.Player?.Entity?.Pos is not { } at || !Finite(clock)) return;
        var far = double.IsNaN(_made.X) || Math.Abs(at.X - _made.X) + Math.Abs(at.Z - _made.Z) > Moved;
        if (!far && clock - _made.At < Refresh) return;
        _made = (at.X, at.Z, clock);
        var file = Path.Join(GamePaths.DataPath, "Maps", capi.World.SavegameIdentifier + ".db");
        if (!File.Exists(file)) return;
        var (x, z) = (at.X, at.Z);
        _job = Task.Run(() => Build(file, x, z));
    }

    // The atlas of a map database around x, z (the player), how far it reaches and where its centre is; no pixels when
    // the map holds nothing
    // Only the pieces a texel centre falls in are read in full: positions first (the reach needs them all), then their
    // colours one by one, so a large map costs what the atlas shows, not every piece explored
    internal static (int[]? Pixels, double Reach, (double X, double Z) Center) Build(string file, double x, double z)
    {
        using var map = Open(file);
        var positions = Positions(map);
        if (positions.Count == 0 || !Finite(x + z)) return (null, MinReach, (x, z));
        var reach = Reached(positions, x, z);
        var (pixels, step) = (new int[Count], 2 * reach / Size);
        var (blob, colors) = (new byte[MaxBlob], new int[Chunk * Chunk]); // one of each for every piece read
        foreach (var (cx, cz, position) in positions.Bounded(MaxPieces))
        {
            var corner = ((double)cx * Chunk - (x - reach), (double)cz * Chunk - (z - reach));
            if (!Shown(corner, step)) continue;
            // position is the table's integer primary key, so its rowid: the blob streams into the buffer, no copy made
            using var piece = new SqliteBlob(map, "mappiece", "data", (long)position, readOnly: true);
            var length = (int)Math.Min(piece.Length, MaxBlob + 1L);
            if (length is <= 0 or > MaxBlob) continue;
            piece.ReadExactly(blob, 0, length);
            if (Colours(blob.AsSpan(0, length), colors) || Deserialized(blob.AsSpan(0, length), colors))
                Lay(pixels, corner, step, colors);
        }

        return (pixels, reach, (x, z));
    }

    // MapPieceDB as protobuf-net writes it: field 1 with one varint per colour, a negative colour ten bytes long. A packed
    // field 1 (one length-prefixed run) is read too. Anything else answers false, and the serializer reads the piece.
    internal static bool Colours(ReadOnlySpan<byte> data, int[] into)
    {
        var (at, n) = (0, 0);
        for (var guard = 0; guard < MaxBlob && at < data.Length && n < into.Length; guard++)
        {
            var tag = data[at++];
            if (tag == 0x08)
            {
                if (!Varint(data, ref at, out var value)) return false;
                into[n++] = (int)value;
            }
            else if (tag == 0x0A)
            {
                if (!Varint(data, ref at, out var run) || run > (ulong)(data.Length - at)) return false;
                var end = at + (int)run;
                for (var k = 0; k < MaxBlob && at < end && n < into.Length; k++)
                {
                    if (!Varint(data, ref at, out var value)) return false;
                    into[n++] = (int)value;
                }
            }
            else return false;
        }

        return n == into.Length && at == data.Length;
    }

    private static bool Varint(ReadOnlySpan<byte> data, ref int at, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 70 && at < data.Length; shift += 7)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }

        return false;
    }

    // A row that is no MapPieceDB (MapDB.SetMapBlockIdMappingDB writes its mapping at position 1, chunk 1, 0) or a
    // corrupt one throws in protobuf-net: that piece is left out, not the whole atlas at every refresh
    private static bool Deserialized(ReadOnlySpan<byte> data, int[] into)
    {
        MapPieceDB? piece;
        try
        {
            piece = SerializerUtil.Deserialize<MapPieceDB>(data.ToArray());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }

        if (piece?.Pixels is not { Length: Chunk * Chunk } read) return false;
        read.CopyTo(into, 0);
        return true;
    }

    // The texel range whose centres fall in a chunk at corner (Lay's), empty when none does
    private static ((int From, int To) X, (int From, int To) Z) Texels((double Left, double Top) corner, double step)
    {
        var (left, top) = corner;
        return ((Math.Max(0, (int)Math.Ceiling(left / step - 0.5)), Math.Min(Size - 1, (int)Math.Floor((left + Chunk) / step - 0.5))),
            (Math.Max(0, (int)Math.Ceiling(top / step - 0.5)), Math.Min(Size - 1, (int)Math.Floor((top + Chunk) / step - 0.5))));
    }

    private static bool Shown((double Left, double Top) corner, double step)
    {
        var (across, down) = Texels(corner, step);
        return across.From <= across.To && down.From <= down.To;
    }

    // A chunk's colours into the texels whose centres fall in it; corner: the chunk's offset from the atlas' corner in
    // blocks, step: blocks per texel
    private static void Lay(int[] pixels, (double Left, double Top) corner, double step, int[] colors)
    {
        var (left, top) = corner;
        var ((from, to), (up, down)) = Texels(corner, step);
        for (var tz = up; tz <= Math.Min(down, Size - 1); tz++)
            for (var tx = from; tx <= Math.Min(to, Size - 1); tx++)
                pixels[tz * Size + tx] = Mean(colors, (tx + 0.5) * step - left, (tz + 0.5) * step - top, step) | unchecked((int)0xFF000000);
    }

    // The map's colour around a point of the chunk, averaged over a 3 x 3 grid across the texel's width: the map's
    // per-column speckle evens out at a globe's scale
    private static int Mean(int[] colors, double x, double z, double width)
    {
        int r = 0, g = 0, b = 0;
        var spread = Math.Min(width, Chunk) / 3;
        for (var k = 0; k < 9; k++)
        {
            int across = k % 3 - 1, down = k / 3 - 1;
            var (px, pz) = ((int)(x + across * spread), (int)(z + down * spread));
            var c = colors[Math.Clamp(pz, 0, Chunk - 1) * Chunk + Math.Clamp(px, 0, Chunk - 1)];
            (r, g, b) = (r + (c & 0xFF), g + ((c >> 8) & 0xFF), b + ((c >> 16) & 0xFF));
        }

        return r / 9 | (g / 9 << 8) | (b / 9 << 16);
    }

    // no pooling: a pooled connection keeps the file open after Dispose, and on Windows the game then cannot open its own
    // map database for writing when the world is joined again ("it seems to be not writable")
    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    // Every stored map piece's chunk x, z and its key, without its colours
    private static List<(int X, int Z, ulong Position)> Positions(SqliteConnection map)
    {
        List<(int, int, ulong)> pieces = [];
        using var command = map.CreateCommand();
        command.CommandText = "SELECT position FROM mappiece";
        using var reader = command.ExecuteReader();
        for (var i = 0; i < MaxPieces && reader.Read(); i++)
        {
            var position = (ulong)reader.GetInt64(0);
            pieces.Add(((int)(position & ((1UL << 27) - 1)), (int)(position >> 27), position));
        }

        return pieces;
    }

    // How far the explored land reaches from the player: the distance most pieces lie within, so one far trip does not
    // shrink the rest to a dot
    private static double Reached(List<(int X, int Z, ulong Position)> pieces, double x, double z)
    {
        var distances = new double[Math.Min(pieces.Count, MaxPieces)];
        for (var i = 0; i < Math.Min(distances.Length, MaxPieces); i++)
            distances[i] = Math.Max(Math.Abs((double)pieces[i].X * Chunk + Chunk / 2.0 - x), Math.Abs((double)pieces[i].Z * Chunk + Chunk / 2.0 - z));
        Array.Sort(distances);
        var far = distances.Length > 0 ? distances[(int)(distances.Length * 0.97)] : MinReach;
        return Math.Clamp(far + Chunk, MinReach, MaxReach);
    }

    private void Upload(int[] pixels)
    {
        (_texture.Width, _texture.Height) = (Size, Size);
        capi.Render.LoadOrUpdateTextureFromRgba(pixels, false, 0, ref _texture);
    }

    private void Warn(string? why)
    {
        if (_warned) return;
        _warned = true;
        capi.Logger?.Warning("Komet styles: the world map could not be read for the globe: {0}", why ?? "no pieces yet");
    }

    public void Dispose()
    {
        _texture.Dispose();
    }
}
