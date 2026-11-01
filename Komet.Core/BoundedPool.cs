namespace Komet.Core;

// At most max items under one lock, no allocation once warm (ConcurrentStack allocates per push, walks for Count)
internal sealed class BoundedPool<T>(int max) where T : class
{
    private readonly Stack<T> _items = new();
    private readonly Lock _lock = new();

    public T? Take()
    {
        lock (_lock)
        {
            return _items.TryPop(out var item) ? item : null;
        }
    }

    // Drops every kept item, returning how many were kept
    public int Clear()
    {
        lock (_lock)
        {
            var count = _items.Count;
            _items.Clear();
            return count;
        }
    }

    public void Give(T item)
    {
        if (!NotNull(item)) return;
        lock (_lock)
            if (_items.Count < max)
                _items.Push(item);
    }
}
