namespace Komet.Core;

// The typed links from a lower module up to a higher one. A module declares the interfaces it asks, such as Rendering's
// IVulkanSide or Core's IShaderBackend, each with a null object for the answer without that module.
// The mod project's composition root (Modules.Bind) registers the implementations, KometModSystem's Dispose clears them.
// A module never names a module above it: the compiler refuses the reference.
// Reads come from any thread (a reference read); Register and Clear run on the main thread at start and close.
internal static class Services
{
    private const int MaxServices = 32;

    private static readonly List<Action> Resets = [];
    private static readonly HashSet<Type> Known = [];
    private static readonly Lock Gate = new();

    // Replaces a registration of the same interface
    public static void Register<T>(T service) where T : class
    {
        if (NotNull(service)) _ = Swap(service);
    }

    // The registered implementation; null when none is (the caller's null object answers)
    public static T? Find<T>() where T : class => Slot<T>.Read();

    // Sets or (null) removes a registration and returns the one before it: the tests' stand-ins
    public static T? Swap<T>(T? service) where T : class
    {
        lock (Gate)
        {
            if (Known.Add(typeof(T)) && Assert(Resets.Count < MaxServices))
            {
                Resets.Add(static () => Slot<T>.Exchange(null));
            }
        }

        return Slot<T>.Exchange(service);
    }

    // Every registration gone: the world closed, the next StartPre registers again
    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var reset in Resets.Bounded(MaxServices))
            {
                reset();
            }
        }
    }

    // One field each interface
    private static class Slot<T> where T : class
    {
        private static T? _value;

        public static T? Read() => Volatile.Read(ref _value);

        public static T? Exchange(T? service) => Interlocked.Exchange(ref _value, service);
    }
}
