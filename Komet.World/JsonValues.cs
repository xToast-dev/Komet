using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace Komet.World;

// Every block, item and entity variant holds its own parsed attributes (the client parses each from its packet): the same
// strings, booleans and numbers again and again, each its own object. Once the level is final, a single pass over the
// collectibles' attributes, block entity behaviour properties and entity type attributes and behaviours swaps every
// value for the first equal one (string by ordinal, number by its exact bits, same type). Only Value changes, to an
// equal object of its type: trees, names and order stay, mods read and write them as before.
internal static class JsonValues
{
    private const int MaxTokens = 1 << 24, MaxRoots = 1 << 20, MaxOpen = 1 << 16;
    private static readonly object True = true, False = false;
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Values swapped in the last pass
    public static int Shared { get; private set; }

    public static void Install(ICoreClientAPI? api, ILogger? logger)
    {
        Stop();
        if (!NotNull(api)) return;
        (_api, _logger) = (api, logger);
        api.Event.LevelFinalize += Finalized;
        Installed = true;
    }

    public static void Stop()
    {
        if (_api is { } api) api.Event.LevelFinalize -= Finalized;
        (_api, _logger, Installed, Shared) = (null, null, false, 0);
    }

    private static void Finalized()
    {
        if (!Enabled || _api?.World is not { } world) return;
        var pass = new Pass();
        try
        {
            foreach (var collectible in world.Collectibles.Bounded(MaxRoots))
            {
                pass.Walk(collectible?.Attributes?.Token);
                if (collectible is not Block block || block.BlockEntityBehaviors is not { } behaviors) continue;
                foreach (var behavior in behaviors.Bounded(MaxRoots)) pass.Walk(behavior?.properties?.Token);
            }

            foreach (var type in world.EntityTypes.Bounded(MaxRoots))
            {
                pass.Walk(type?.Attributes?.Token);
                pass.Walk(type?.Client?.BehaviorsAsJsonObj);
                pass.Walk(type?.Server?.BehaviorsAsJsonObj);
            }
        }
        catch (InvalidOperationException e) // a mod changed a tree while it was walked: what is done stays done
        {
            _logger?.Notification("Komet JsonValues: stopped early, {0}", e.Message);
        }

        Shared = pass.Swapped;
        _logger?.Notification("Komet JsonValues: {0} attribute values share an equal one", pass.Swapped);
    }

    // One walk's tables; dropped after it, so only values still in use stay alive
    internal sealed class Pass
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<long, object> _longs = [], _doubles = [];
        private readonly Stack<JToken> _open = new();
        private int _tokens;

        public int Swapped { get; private set; }

        public void Walk(JsonObject[]? objects)
        {
            if (objects is null) return;
            foreach (var item in objects.Bounded(MaxRoots)) Walk(item?.Token);
        }

        // Depth first through First/Next, no enumerator allocated per container
        public void Walk(JToken? root)
        {
            if (root is null) return;
            _open.Clear();
            _open.Push(root);
            for (var step = 0; step < MaxTokens; step++)
            {
                if (_tokens >= MaxTokens || !_open.TryPop(out var token)) return;
                _tokens++;
                if (token is JValue value) Share(value);
                else if (token is JContainer container) Open(container.First);
            }
        }

        // A container's children, the last one on top
        private void Open(JToken? child)
        {
            for (var i = 0; i < MaxOpen; i++)
            {
                if (child is null || _open.Count >= MaxOpen) return;
                _open.Push(child);
                child = child.Next;
            }
        }

        private void Share(JValue value)
        {
            var current = value.Value;
            var kept = current switch
            {
                string text => Kept(_strings, text, text),
                bool flag => flag ? True : False,
                long number => Kept(_longs, number, current),
                double number => Kept(_doubles, BitConverter.DoubleToInt64Bits(number), current),
                _ => current
            };
            if (ReferenceEquals(kept, current)) return;
            value.Value = kept; // same type: only the reference changes
            Swapped++;
        }

        private static TValue Kept<TKey, TValue>(Dictionary<TKey, TValue> table, TKey key, TValue current)
            where TKey : notnull
        {
            if (table.TryGetValue(key, out var kept)) return kept;
            table[key] = current;
            return current;
        }
    }
}
