using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Common;

namespace Komet.World;

// WorldChunk's private members the World features read: the lock its packing and unpacking take, its unpacked data,
// the format version of its packed data, and the unpack its readers run under that lock.
internal static class ChunkInternals
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
    public static extern ref object PackUnpackLock(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkdata")]
    public static extern ref ChunkData? Data(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkdataVersion")]
    public static extern ref int Version(WorldChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "unpackNoLock")]
    public static extern void Unpack(WorldChunk chunk);

    // Whether every member is there with the type the accessors expect: a missing one throws at its first use
    public static bool Present()
    {
        var chunk = typeof(WorldChunk);
        var found = AccessTools.DeclaredField(chunk, "packUnpackLock")?.FieldType == typeof(object) &&
                    AccessTools.DeclaredField(chunk, "chunkdata")?.FieldType == typeof(ChunkData) &&
                    AccessTools.DeclaredField(chunk, "chunkdataVersion")?.FieldType == typeof(int) &&
                    AccessTools.DeclaredMethod(chunk, "unpackNoLock", []) is not null;
        return found;
    }
}
