namespace Komet;

// Disposables made in one place and handed on only once their setup worked: on a refusal or an exception they go again
internal static class Owned
{
    private const int MaxItems = 64;

    // made goes back when setup gives no reason against it (null or empty); else it is disposed and why holds the reason.
    // An exception from setup disposes it too.
    public static T? Kept<T, TState>(T? made, TState state, System.Func<T, TState, string?> setup, out string why)
        where T : class, IDisposable
    {
        why = "";
        if (made is null) return null;
        if (!NotNull(setup))
        {
            made.Dispose();
            return null;
        }

        try
        {
            why = setup(made, state) ?? "";
        }
        catch
        {
            made.Dispose();
            throw;
        }

        if (why.Length == 0) return made;
        made.Dispose();
        return null;
    }

    // Each one disposed, even when one before it throws
    public static void DropAll(IDisposable?[] items)
    {
        if (!NotNull(items)) return;
        List<Exception>? failed = null;
        foreach (var item in items.Bounded(MaxItems))
            try
            {
                item?.Dispose();
            }
            catch (Exception e)
            {
                (failed ??= []).Add(e);
            }

        if (failed is not null) throw new AggregateException(failed);
    }
}
