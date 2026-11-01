using System.Collections.Concurrent;
using Komet.World;
using Newtonsoft.Json.Linq;
using Vintagestory.GameContent;

namespace Komet.Test.Synth;

// World-side features on the engine's own objects, built without a game as their tests build them
[Category(Synth.Category)]
[NonParallelizable]
public sealed class WorldSynth
{
    private int _mainThread;
    private FrameProfilerUtil? _profiler;

    [SetUp]
    public void MainThread()
    {
        (_mainThread, _profiler) = (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler);
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static _ => { });
    }

    [TearDown]
    public void Restore() => (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler) = (_mainThread, _profiler!);

    // ---- ClimateCache: the tessellation thread's climate map per chunk, at view distance 1536 (7x7 regions in view), the outer half
    // of the view (24..48 chunks out, where a join or a flight keeps tessellating) nearest first, eight chunks a column, the walk going
    // on across batches. Off, ten maps fit and one distance's columns lie in regions all around.
    [Test]
    public void ClimateCacheViewDistance1536()
    {
        const int regions = 7, regionChunks = 16, radius = 48;
        var saved = ClientSettings.ViewDistance;
        ClientSettings.ViewDistance = 1536;
        try
        {
            using var rig = new ChunkRig();
            ChunkRig.Set(rig.Map, "regionMapSizeX", 64);
            ChunkRig.Set(rig.Map, "regionMapSizeZ", 64);
            for (var rx = 0; rx < regions; rx++)
                for (var rz = 0; rz < regions; rz++)
                    rig.AddRegion(rx * regionChunks, rz * regionChunks, rx * regions + rz);
            var (cx0, cz0) = (regions * regionChunks / 2, regions * regionChunks / 2); // the centre column, in chunks
            var columns = new List<(int X, int Z)>();
            for (var dx = -radius; dx <= radius; dx++)
                for (var dz = -radius; dz <= radius; dz++)
                    if (dx * dx + dz * dz <= radius * radius && dx * dx + dz * dz >= radius * radius / 4)
                        columns.Add((cx0 + dx, cz0 + dz));
            columns.Sort((a, b) => Near(a).CompareTo(Near(b)));
            var maps = AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMaps");
            var load = AccessTools.Method(typeof(ClientWorldMap), nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread));
            var next = 0;
            _ = Synth.Run("ClimateCache", "vd1536 outer half", ops =>
            {
                for (var i = 0; i < ops; i++)
                {
                    var (x, z) = columns[next / 8 % columns.Count];
                    next++;
                    _ = rig.Map.LoadOrCreateLerpedClimateMapOffthread(x, z);
                }
            }, 2000, () => rig.Map.LoadOrCreateLerpedClimateMapOffthread(cx0 + 20, cz0 - 30)[12345], () =>
            {
                // the engine's ten again while the feature is out (its dictionary stays while in)
                if (Harmony.GetPatchInfo(load)?.Prefixes.Count is not > 0)
                    maps.SetValue(rig.Map, new LimitedDictionary<long, int[]>(ClimateCache.EngineCapacity));
            });
            return;

            // nearest first, as the tessellation queue takes them: one distance's columns lie all around the player
            long Near((int X, int Z) c) =>
                ((long)(c.X - cx0) * (c.X - cx0) + (long)(c.Z - cz0) * (c.Z - cz0)) * 1000000 + c.X * 1000 + c.Z;
        }
        finally
        {
            ClientSettings.ViewDistance = saved;
        }
    }

    // ---- PartitionReuse: EntityPartitioning.PartitionEntities over a base with 300 creatures, items and two players moving
    [Test]
    public void PartitionReuseBase()
    {
        var partition = AccessTools.MethodDelegate<Action<EntityPartitioning, ICollection<Entity>>>(
            AccessTools.Method(typeof(EntityPartitioning), "PartitionEntities"));
        var blocks = Answers.Of<IBlockAccessor>(new()
            { ["get_MapSizeX"] = _ => 1024000, ["get_MapSizeZ"] = _ => 1024000 });
        var world = Answers.Of<IWorldAccessor>(new() { ["get_BlockAccessor"] = _ => blocks });
        var api = Answers.Of<ICoreAPI>(new() { ["get_World"] = _ => world });
        var partitioning = new EntityPartitioning();
        AccessTools.Field(typeof(EntityPartitioning), "api").SetValue(partitioning, api);
        var entities = new List<Entity> { new EntityPlayer(), new EntityPlayer() };
        for (var i = 0; i < 300; i++) entities.Add(i % 3 == 0 ? new EntityItem() : new EntityAgent());
        for (var i = 0; i < entities.Count; i++) entities[i].touchDistance = i % 7 * 0.25;
        var tick = 0;
        _ = Synth.Run("PartitionReuse", "base 300 entities", ops =>
        {
            for (var op = 0; op < ops; op++)
            {
                tick++;
                for (var i = 0; i < entities.Count; i++)
                {
                    var jump = (i + tick) % 50 == 0 ? 40 * (tick % 7) : 0;
                    _ = entities[i].Pos.SetPos(512000 + i % 13 * 3.5 + jump, 110 + i % 9 * 4.0,
                        512000 + i % 11 * 5.25 + tick % 32);
                }

                partition(partitioning, entities);
            }
        }, 50, () => (partitioning.Partitions.Count, partitioning.LargestTouchDistance), () => tick = 0);
    }

    // ---- CookingMatch: a firepit's pot asks every cooking recipe, twice a burn tick
    [Test]
    public void CookingMatchPot()
    {
        var cooking = new Cooking();
        var (recipes, pots) = (cooking.Recipes(), cooking.Pots(new Random(7), 64));
        var (next, matched) = (0, 0);
        _ = Synth.Run("CookingMatch", "pot 3 recipes", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var pot = pots[next++ % pots.Length];
                foreach (var recipe in recipes)
                {
                    var servings = 0;
                    if (recipe.Matches(pot, ref servings)) matched += servings;
                }
            }
        }, 200, () => matched, () => (next, matched) = (0, 0));
    }

    // ---- AlloyMatch: a crucible on a firepit asks every alloy recipe (vanilla has 6) whether its contents match, every burn tick
    [Test]
    public void AlloyMatchCrucible()
    {
        var random = new Random(3);
        var metals = Enumerable.Range(1, 8).Select(i => new Item(i)).ToArray();
        var ores = metals.Select((m, i) => new Item(100 + i)
        {
            CombustibleProps = new CombustibleProperties
            {
                SmeltedRatio = 5, SmeltedStack = new JsonItemStack { ResolvedItemstack = new ItemStack(m) }
            }
        }).ToArray();
        var recipes = Enumerable.Range(0, 6).Select(_ => new AlloyRecipe
        {
            Ingredients = [.. metals.OrderBy(_ => random.Next()).Take(random.Next(2, 4)).Select(m => new MetalAlloyIngredient
            {
                ResolvedItemstack = new ItemStack(m), MinRatio = 0.1f * random.Next(0, 5), MaxRatio = 0.5f + 0.1f * random.Next(0, 6)
            })]
        }).ToArray();
        var crucibles = new ItemStack[64][];
        for (var c = 0; c < crucibles.Length; c++)
        {
            crucibles[c] = new ItemStack[4];
            for (var s = 0; s < 4; s++)
            {
                var i = random.Next(3);
                crucibles[c][s] = new ItemStack(random.Next(3) == 0 ? ores[i] : metals[i], random.Next(1, 40));
            }
        }

        var (next, matched) = (0, 0);
        _ = Synth.Run("AlloyMatch", "crucible 6 recipes", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var crucible = crucibles[next++ % crucibles.Length];
                foreach (var recipe in recipes)
                    if (recipe.Matches(crucible, next % 2 == 0))
                        matched++;
            }
        }, 500, () => matched, () => (next, matched) = (0, 0));
    }

    // ---- EventBusSweep and ListenerSlots' event bus: display block entities (shelves, racks, firepits) coming and going as chunks
    // stream; 3000 came and went earlier in the session. Per op one loads (registers), the oldest of 64 live ones unloads, the
    // frame's tasks run and one event is pushed.
    [Test]
    public void EventBusSweepStreaming() => Streaming("EventBusSweep");

    [Test]
    public void ListenerSlotsEventBus() => Streaming("ListenerSlots");

    private static void Streaming(string feature)
    {
        var bus = new Bus();
        var live = new Queue<Display>();
        _ = Synth.Run(feature, "display streaming 3000 gone", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                live.Enqueue(bus.Display());
                live.Dequeue().OnBlockUnloaded();
                bus.Game.ExecuteMainThreadTasks(0);
                bus.Api.Event.PushEvent("onsettransform");
            }
        }, 100, () => live.Sum(d => d.Heard), () =>
        {
            bus.Game.eventManager.EventBusListeners.Clear();
            live.Clear();
            for (var i = 0; i < 3000; i++) bus.Display().OnBlockUnloaded();
            bus.Game.ExecuteMainThreadTasks(0);
            for (var i = 0; i < 64; i++) live.Enqueue(bus.Display());
        });
    }

    // ---- ListenerSlots' tick slots: 30000 block tick listeners (the client's list never shrinks); a chunk swap removes 20 at random
    // and adds 20, each add searching the first empty slot
    [Test]
    public void ListenerSlotsTickSlots()
    {
        var events = Events();
        var random = new Random(5);
        var ids = new List<long>();
        long id = 0;
        _ = Synth.Run("ListenerSlots", "tick slots 30000", ops =>
        {
            for (var op = 0; op < ops; op++)
            {
                for (var k = 0; k < 20; k++)
                {
                    var at = random.Next(ids.Count);
                    events.RemoveGameTickListener(ids[at]);
                    ids[at] = ids[^1];
                    ids.RemoveAt(ids.Count - 1);
                }

                for (var k = 0; k < 20; k++) ids.Add(AddBlockListener(events, Listener(++id, 1000), id));
            }
        }, 20, () => (BlockList(events).Count, BlockList(events).Count(l => l is null)), () =>
        {
            (id, ids) = (0, []);
            random = new Random(5);
            BlockList(events) = [];
            BlockIndices(events).Clear();
            for (var i = 0; i < 30000; i++) ids.Add(AddBlockListener(events, Listener(++id, 1000), id));
        });
    }

    // ---- BlockEntityCaches (ignitable part): a firewood pile's ground storage initialising as its chunk loads
    [Test]
    public void BlockEntityCachesGroundStorage()
    {
        var item = new Item(1) { Code = new AssetLocation("game", "firewood") };
        var storable = new CollectibleBehaviorGroundStorable(item)
        {
            propertiesAtString = """
                { "layout": "Stacking", "placeRemoveSound": "sounds/block/planks", "stackingModel": "block/wood/firewoodpile",
                  "stackingTextures": { "bark": "block/wood/bark/oak", "wood": "block/wood/debarked/oak" }, "tessQuantityElements": 1,
                  "stackingCapacity": 32, "transferQuantity": 2, "bulkTransferQuantity": 4, "burnHoursPerItem": 1.5,
                  "collisionBox": { "x1": 0, "y1": 0, "z1": 0, "x2": 1, "y2": 0.125, "z2": 1 }, "cbScaleYByLayer": 0.125,
                  "upSolid": true }
                """
        };
        item.CollectibleBehaviors = [storable];
        var piles = Enumerable.Range(0, 16).Select(_ => new BlockEntityGroundStorage()).ToArray();
        foreach (var pile in piles) pile.Inventory[0].Itemstack = new ItemStack(item, 32);
        var next = 0;
        _ = Synth.Run("BlockEntityCaches", "firewood pile init", ops =>
        {
            for (var i = 0; i < ops; i++) UpdateIgnitable(piles[next++ % piles.Length]);
        }, 500, () => BurnHours(piles[3]), () => next = 0);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateIgnitable")]
    private static extern void UpdateIgnitable(BlockEntityGroundStorage storage);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "burnHoursPerItem")]
    private static extern ref float BurnHours(BlockEntityGroundStorage storage);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AddGameTickListenerBlockInternal")]
    private static extern long AddBlockListener(EventManager events, GameTickListenerBlock listener, long newListenerId);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "GameTickListenersBlock")]
    private static extern ref List<GameTickListenerBlock> BlockList(EventManager events);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "GameTickListenersBlockIndices")]
    private static extern ref ConcurrentDictionary<long, int> BlockIndices(EventManager events);

    private static GameTickListenerBlock Listener(long id, int interval) =>
        new(id, interval, 0, new BlockPos((int)id, 100, 0), static (_, _, _) => { }, null!);

    // A client event manager without its game: the tick lists and their indices, the callbacks, nothing else
    private static ClientEventManager Events()
    {
        var events = (ClientEventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        foreach (var (name, value) in (ReadOnlySpan<(string, object)>)
                 [
                     ("GameTickListenersEntity", new List<GameTickListener>()), ("GameTickListenersBlock", new List<GameTickListenerBlock>()),
                     ("GameTickListenersEntityIndices", new ConcurrentDictionary<long, int>()),
                     ("GameTickListenersBlockIndices", new ConcurrentDictionary<long, int>()),
                     ("DelayedCallbacksEntity", new ConcurrentDictionary<long, DelayedCallback>()),
                     ("DelayedCallbacksBlock", new List<DelayedCallbackBlock>()),
                     ("SingleDelayedCallbacksBlock", new Dictionary<BlockPos, DelayedCallbackBlock>()),
                     ("deletable", new List<DelayedCallback>())
                 ])
            AccessTools.Field(typeof(EventManager), name).SetValue(events, value);
        return events;
    }

    // EventBusSweepTests' game: ClientMain's main thread tasks and event bus, ClientEventAPI over it
    private sealed class Bus
    {
        public Bus()
        {
            Game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            (Game.GameLaunchTasks, Game.MainThreadTasks, Game.MainThreadTasksLock) = (new(), new(), new object());
            AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue").SetValue(Game, new Queue<ClientTask>());
            Game.eventManager = (ClientEventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
            Game.eventManager.EventBusListeners = [];
            var events = (ClientEventAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventAPI));
            AccessTools.Field(typeof(ClientEventAPI), "game").SetValue(events, Game);
            Api = Answers.Of<ICoreClientAPI>(new()
            {
                ["get_Event"] = _ => events, ["get_World"] = _ => Answers.Of<IClientWorldAccessor>([]),
                ["get_Side"] = _ => EnumAppSide.Client
            });
        }

        public ClientMain Game { get; }
        public ICoreClientAPI Api { get; }

        // What BlockEntityDisplay.Initialize does on the client
        public Display Display()
        {
            var entity = new Display { Api = Api };
            Api.Event.RegisterEventBusListener(entity.OnEvent);
            return entity;
        }
    }

    private sealed class Display : BlockEntity
    {
        public int Heard { get; private set; }

        public void OnEvent(string eventName, ref EnumHandling handling, IAttribute data) =>
            (Heard, handling) = (Heard + eventName.Length + (data is null ? 0 : 1), EnumHandling.PassThrough);
    }

    // CookingMatchTests' recipes and pots
    private sealed class Cooking
    {
        private readonly ICoreAPI _api = Answers.Of<ICoreAPI>(new() { ["get_World"] = _ => null });
        private readonly Dictionary<string, Item> _items = [];

        public CookingRecipe[] Recipes() =>
        [
            new()
            {
                Code = "soup",
                Ingredients =
                [
                    Ingredient("water", 1, 1, 1f, Exact("waterportion", 100)),
                    Ingredient("veg", 1, 2, 0, Wild("vegetable-*")),
                    Ingredient("meat", 0, 1, 0, Cooked("redmeat-raw", "redmeat-cooked"))
                ]
            },
            new()
            {
                Code = "porridge",
                Ingredients =
                [
                    Ingredient("grain", 1, 2, 0, Wild("grain-*"), Exact("flour", 2)),
                    Ingredient("fruit", 0, 2, 0, Wild("fruit-*"))
                ]
            },
            new()
            {
                Code = "stew",
                Ingredients =
                [
                    Ingredient("meat", 2, 3, 0, Cooked("redmeat-raw", "redmeat-cooked")),
                    Ingredient("veg", 0, 2, 0, Wild("vegetable-*"))
                ]
            }
        ];

        // Pots filled for one of the recipes, a serving count apart, now and then one stack a portion off; a cooked stack's
        // temperature attribute now and then
        public ItemStack?[][] Pots(Random r, int count)
        {
            string[][] recipes =
            [
                ["waterportion", "vegetable-carrot", "vegetable-onion", "redmeat-cooked"],
                ["grain-spelt", "fruit-cherry", "fruit-cherry"], ["redmeat-raw", "redmeat-raw", "vegetable-onion"]
            ];
            var pots = new ItemStack?[count][];
            for (var i = 0; i < count; i++)
            {
                var q = r.Next(1, 4);
                var chosen = recipes[r.Next(recipes.Length)].Where((_, k) => k < 2 || r.Next(2) == 0).ToList();
                var pot = new ItemStack?[r.Next(Math.Max(4, chosen.Count), 7)];
                for (var k = 0; k < chosen.Count; k++)
                {
                    var size = chosen[k] == "waterportion" ? 100 * q : q;
                    pot[k] = new ItemStack(Item(chosen[k]), r.Next(8) == 0 ? size + 1 : size);
                    if (r.Next(4) == 0) pot[k]!.Attributes.SetFloat("temperature", 20);
                }

                pots[i] = [.. pot.OrderBy(_ => r.Next())];
            }

            return pots;
        }

        private static CookingRecipeIngredient Ingredient(string code, int min, int max, float litres,
            params CookingRecipeStack[] stacks) =>
            new() { Code = code, MinQuantity = min, MaxQuantity = max, PortionSizeLitres = litres, ValidStacks = stacks };

        private CookingRecipeStack Exact(string code, int size) => new()
        {
            Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = size,
            ResolvedItemstack = new ItemStack(Item(code), size)
        };

        private static CookingRecipeStack Wild(string code) =>
            new() { Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = 1 };

        private CookingRecipeStack Cooked(string raw, string cooked)
        {
            var stack = Exact(raw, 1);
            stack.CookedStack = new JsonItemStack
            {
                Code = new AssetLocation("game", cooked), Type = EnumItemClass.Item,
                ResolvedItemstack = new ItemStack(Item(cooked))
            };
            return stack;
        }

        private Item Item(string code)
        {
            if (_items.TryGetValue(code, out var item)) return item;
            item = new Item(_items.Count + 1) { Code = new AssetLocation("game", code) };
            if (code == "waterportion")
                item.Attributes = new JsonObject(JToken.Parse("{ waterTightContainerProps: { itemsPerLitre: 100 } }"));
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, _api);
            _items[code] = item;
            return item;
        }
    }
}
