using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Asking a renderer whether it is idle costs a cache miss per object (handler, renderer, an AnimationUtil's animator, renderer and
// dictionary), each waiting on the one before. Frame is called in list order, so a cursor prefetches ahead: the handler Far entries
// on, its renderer Near on, a util's parts Next on. A cursor that no longer matches (list changed mid-walk) stops prefetching. A
// stage whose entries are parked (IdleAnimators.Parked) prefetches in its poll instead: what reaches Frame there is mostly at work.
internal static partial class IdleAnimators
{
    private const int Far = 16, Near = 8, Next = 4, Line = 64, MaxOffset = 1024;

    private static List<RenderHandler>? _walked;
    private static int _cursor;

    // Where the poll's two fields lie in their objects, from the object's address (main thread, from Install): ShouldRender and the
    // animator's activeAnimCount sit behind a dozen references, mostly on the object's second or third line
    private static nint _shownAt, _countAt;

    private const string CountName = "activeAnimCount";

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = CountName)]
    private static extern ref int ActiveCount(AnimatorBase animator);

    // A stage that is not polled lets its parked objects go (Release): every stage while nothing parks, at Before the stages the
    // engine skipped last frame
    internal static void Staged(List<RenderHandler>[] ___renderersByStage, EnumRenderStage stage)
    {
        (_walked, _list, _current) = (null, null, null);
        _bits = [];
        var polls = Enabled && !Blocked && _parks && !_crossed;
        if (stage == EnumRenderStage.Before) _frame++;
        if (!polls || stage == EnumRenderStage.Before) Release(!polls);
        if (!Enabled || Blocked || !NotNull(___renderersByStage) || !Index((int)stage, ___renderersByStage.Length)) return;
        var list = ___renderersByStage[(int)stage];
        if (!NotNull(list)) return;
        if (!(polls && Poll(list, stage)) && Sse.IsSupported) (_walked, _cursor) = (list, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ahead(IRenderer? renderer)
    {
        if (_walked is not { } list) return;
        var items = CollectionsMarshal.AsSpan(list);
        var at = _cursor;
        if ((uint)at >= (uint)items.Length || !ReferenceEquals(items[at]?.Renderer, renderer))
        {
            _walked = null; // the list changed under the walk
            return;
        }

        _cursor = at + 1;
        if (at + Far < items.Length) Fetch(items[at + Far]);
        if (at + Near < items.Length && items[at + Near] is { } near) Fetch(near.Renderer, 2);
        if (at + Next < items.Length && items[at + Next]?.Renderer is AnimationUtil util)
        {
            Fetch(util.animator);
            Fetch(util.renderer, 2);
            Fetch(util.activeAnimationsByAnimCode);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Fetch(object? item, int lines = 1)
    {
        if (item is null || !Sse.IsSupported) return;
        var address = (byte*)Unsafe.As<object, nint>(ref item);
        Sse.Prefetch0(address);
        if (lines > 1) Sse.Prefetch0(address + Line);
    }

    // The line of one field, `at` bytes from the object's address
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void FetchAt(object? item, nint at)
    {
        if (item is null || !Sse.IsSupported) return;
        Sse.Prefetch0((byte*)Unsafe.As<object, nint>(ref item) + at);
    }

    // A field's distance from its object's address: from the object's first field (StrongBox's one field lies there in any class)
    // plus the method table pointer in front of it. Uninitialized probes: no constructor runs.
    private static void Measure()
    {
        var shown = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        _shownAt = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(shown).Value,
            ref Unsafe.As<bool, byte>(ref shown.ShouldRender)) + IntPtr.Size;
        if (!Assert(_shownAt > 0 && _shownAt < MaxOffset)) _shownAt = 0;
        // nothing else guards the protected field (the engine body reads the ActiveAnimationCount property): a game that renamed or
        // retyped it would make the accessor throw out of Install, and all it costs here is a prefetch
        _countAt = Assert(AccessTools.DeclaredField(typeof(AnimatorBase), CountName)?.FieldType == typeof(int))
            ? CountAt((AnimatorBase)RuntimeHelpers.GetUninitializedObject(typeof(ClientAnimator)))
            : 0;
    }

    // Apart from Measure: the accessor binds its field when it is compiled, which only a call compiles here
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint CountAt(AnimatorBase probe)
    {
        if (!NotNull(probe)) return 0;
        var at = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(probe).Value,
            ref Unsafe.As<int, byte>(ref ActiveCount(probe))) + IntPtr.Size;
        return Assert(at > 0 && at < MaxOffset) ? at : 0;
    }
}
