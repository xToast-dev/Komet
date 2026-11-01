using Komet.Options;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Komet.Test.Options;

// The globe's atlas from a map database: pieces near the player are laid in their colour, the rest of the square stays
// clear, and a piece no texel centre falls in is never read
public sealed class WorldAtlasTests
{
    [Test]
    public void PiecesAroundThePlayerAreLaid()
    {
        var file = Path.Combine(Path.GetTempPath(), $"komet-atlas-{Environment.ProcessId}.db");
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open();
                using var make = db.CreateCommand();
                make.CommandText = "CREATE TABLE mappiece (position integer PRIMARY KEY, data BLOB)";
                _ = make.ExecuteNonQuery();
                for (var cx = 0; cx < 4; cx++)
                    for (var cz = 0; cz < 4; cz++)
                        Insert(db, cx, cz, 0x00336699 + cx);
            }

            var (pixels, reach, _) = WorldAtlas.Build(file, 64, 64);
            Assert.That(pixels, Is.Not.Null);
            var laid = pixels!.Count(p => p != 0);
            Assert.Multiple(() =>
            {
                Assert.That(reach, Is.GreaterThanOrEqualTo(512), "the reach never shrinks below the minimum");
                Assert.That(laid, Is.GreaterThan(0), "the explored chunks are laid");
                Assert.That(laid, Is.LessThan(WorldAtlas.Size * WorldAtlas.Size), "unexplored texels stay clear");
                Assert.That(pixels.Where(p => p != 0).All(p => ((uint)p >> 24) == 0xFF), Is.True, "laid texels are opaque");
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    // The atlas' own reader gives what the game's serializer wrote, negative (opaque ARGB) colours included
    [Test]
    public void ColoursAreReadAsTheSerializerWroteThem()
    {
        var pixels = new int[32 * 32];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = unchecked((int)(0xFF000000u | (uint)(i * 40503 % 0xFFFFFF)));
        pixels[0] = 0;
        pixels[1] = 1;
        var data = SerializerUtil.Serialize(new MapPieceDB { Pixels = pixels });
        var read = new int[32 * 32];
        Assert.That(WorldAtlas.Colours(data, read), Is.True);
        Assert.That(read, Is.EqualTo(pixels));
    }

    private static void Insert(SqliteConnection db, int cx, int cz, int colour)
    {
        var pixels = new int[32 * 32];
        Array.Fill(pixels, colour);
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO mappiece (position, data) VALUES ($p, $d)";
        _ = insert.Parameters.AddWithValue("$p", ((long)cz << 27) | (long)cx);
        _ = insert.Parameters.AddWithValue("$d", SerializerUtil.Serialize(new MapPieceDB { Pixels = pixels }));
        _ = insert.ExecuteNonQuery();
    }
}
