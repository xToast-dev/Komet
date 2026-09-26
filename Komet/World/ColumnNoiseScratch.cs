using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.World;

// World generation fills every new chunk column in GenTerra.generate (VSEssentials): a Parallel.For over its 1024 block columns whose
// body asks terrainNoise.ForColumn for a NewNormalizedSimplexFractalNoise.ColumnNoise. The constructor allocates four arrays per column
// - double[] and int[] of the octave count, OctaveEntry[] and PastEvaluation[] of the octaves that contribute, 928 bytes at the default
// world height - that are garbage once the column is filled: 24.6 of the 132 MB/s flying into new terrain allocates, on the server's
// generation threads, which in singleplayer share the heap and the pauses with the client. Every computation stays the engine's IL,
// only where the four arrays come from changes, and the constructor reads no element of them it has not written first.
// - Rent hands the constructor the thread's array of exactly the length its newarr asks, since NoiseSign and Noise loop to
//   orderedOctaveEntries.Length, zeroed as a new one is. OctaveEntry and PastEvaluation hold only numbers, so the arrays a thread
//   keeps keep nothing of a world alive.
// - Only a constructor that starts while Column has armed its thread gets them, and the first one disarms it. Column replaces the
//   body's one ForColumn call; ForColumn is its arguments and newobj ColumnNoise (checked on its IL), and in the game's assemblies
//   (API, Lib, Essentials, Survival, Creative) nothing else builds this ColumnNoise or calls ForColumn. Any other column - a mod may keep
//   several - is built as shipped.
// - The constructor's arrays never leave it but through its two array fields: it loads, computes, stores into locals, numbers into
//   arrays and fields, and calls only what takes and returns numbers (Math, NoiseValueCurve, the bound setters). Checked on the IL.
// - The body keeps its column in one local, addressed only for ColumnNoise's own double-returning members (BoundMin, BoundMax,
//   NoiseSign), which read the arrays and store no reference; the local is dead when the body returns. Checked on the IL.
// - A thread runs Parallel.For bodies one after another, and the body waits on nothing (noise, lerps, BitArray, its ThreadLocal), so
//   no second body starts on a thread while a column is live there. The engine relies on the same: the body reads the thread's
//   landformWeights from tempDataThreadLocal through the whole y loop, the lifetime of its column. So the next Column on a thread takes
//   arrays whose previous column is dead.
// - Another mod's patch on ForColumn, the constructor, the body or the members could keep a column or build a second one while armed:
//   one found at Install leaves the engine alone, and one added later to ForColumn, the constructor or the body reruns the transpilers
//   here, which stands the arm down for good. The members are checked only at Install: watching them later would mean patching
//   NoiseSign, the y loop's own call, for every column. A patch added there after Install that keeps a copy of a column goes unseen.
internal static class ColumnNoiseScratch
{
    private const string Terra = "Vintagestory.ServerMods.GenTerra", Body = "<generate>";

    private const int MaxInstructions = 2048,
        MaxLength = 64,
        MaxNested = 64,
        MaxMethods = 64,
        MaxReach = 6,
        MaxParameters = 8,
        MaxFields = 16,
        Arrays = 4;

    private const int ArrayHeader = 24; // object header, method table and length of an array on 64 bit

    [ThreadStatic] private static bool _armed;

    [ThreadStatic]
    private static Array?[]? _arrays; // the thread's arrays: for each of the constructor's four, one per length

    private static long _saved;
    private static int _rewritten; // 1 constructor, 2 column body, 4 ForColumn watched
    private static bool _installed, _foreign;
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == 7 && !_foreign;

    public static long Saved =>
        Interlocked.Read(ref _saved); // bytes the constructor would have allocated, a total while Counting.Hud

    // ForColumn and the constructor first, the body last: an arm nobody listens for is only cleared again
    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        (_rewritten, _installed, _foreign, _logger) = (0, false, false, logger);
        var (ctor, body, forColumn) = (Constructor(), ColumnBody(), ForColumn());
        if (!NotNull(harmony) || !NotNull(ctor) || !NotNull(body) || !NotNull(forColumn)) return;
        MethodBase?[] seams = [forColumn, ctor, body, .. Members()];
        if (Foreign(seams, harmony.Id, logger)) return;
        _ = NotNull(harmony.Patch(forColumn,
            transpiler: new HarmonyMethod(typeof(ColumnNoiseScratch), nameof(WatchForColumn))));
        _ = NotNull(harmony.Patch(ctor,
            transpiler: new HarmonyMethod(typeof(ColumnNoiseScratch), nameof(RewriteConstructor))));
        if ((_rewritten & 5) != 5) return;
        _ = NotNull(harmony.Patch(body,
            transpiler: new HarmonyMethod(typeof(ColumnNoiseScratch), nameof(RewriteBody))));
        Volatile.Write(ref _installed, true);
        // A patch another thread added between the check above and here reran the transpilers before _installed said so, but it is
        // on record now: Harmony holds its lock from the transpilers to the record, and GetPatchInfo takes the same lock
        if (!Volatile.Read(ref _foreign) && Foreign(seams, harmony.Id, logger)) Volatile.Write(ref _foreign, true);
    }

    // Whether another mod patches one of the seams, or one is missing; the first one found is logged
    private static bool Foreign(ReadOnlySpan<MethodBase?> seams, string owner, ILogger? logger)
    {
        if (!NotNull(owner) || !Assert(seams.Length <= MaxMethods)) return true;
        for (var i = 0; i < Math.Min(seams.Length, MaxMethods); i++)
        {
            if (!EngineShape.Foreign(seams.Slice(i, 1), EngineShape.Kinds.All, owner)) continue;
            logger?.Notification("Komet ColumnNoiseScratch stands down: {0}; terrain columns allocate as shipped",
                seams[i] is { } seam
                    ? $"another mod patches {seam.DeclaringType?.Name}.{seam.Name}"
                    : "a ColumnNoise member is missing");
            return true;
        }

        return false;
    }

    internal static ConstructorInfo? Constructor()
    {
        var ctor = AccessTools.Constructor(typeof(ColumnNoise),
        [
            typeof(NewNormalizedSimplexFractalNoise), typeof(double), typeof(double[]), typeof(double[]),
            typeof(double), typeof(double)
        ]);
        return NotNull(ctor) && Assert(!ctor.IsStatic) ? ctor : null;
    }

    internal static MethodInfo? ForColumn()
    {
        var method = AccessTools.Method(typeof(NewNormalizedSimplexFractalNoise),
            nameof(NewNormalizedSimplexFractalNoise.ForColumn),
            [typeof(double), typeof(double[]), typeof(double[]), typeof(double), typeof(double)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(ColumnNoise)) ? method : null;
    }

    // The members GenTerra's body calls on its column
    private static MethodBase?[] Members()
    {
        MethodBase?[] members =
        [
            AccessTools.PropertyGetter(typeof(ColumnNoise), nameof(ColumnNoise.BoundMin)),
            AccessTools.PropertyGetter(typeof(ColumnNoise), nameof(ColumnNoise.BoundMax)),
            AccessTools.Method(typeof(ColumnNoise), nameof(ColumnNoise.NoiseSign), [typeof(double), typeof(double)])
        ];
        return Assert(members[0] is null || members[0]!.DeclaringType == typeof(ColumnNoise)) ? members : [null];
    }

    // The lambda generate() hands to Parallel.For, found by what it calls rather than by its compiler-given name
    // (<>c__DisplayClass34_0.<generate>b__0 in 1.22.7); GenTerra lives in VSEssentials, which Komet does not reference
    internal static MethodInfo? ColumnBody()
    {
        var terra = AccessTools.TypeByName(Terra);
        var forColumn = ForColumn();
        if (!NotNull(terra) || !NotNull(forColumn)) return null;
        MethodInfo? found = null;
        foreach (var nested in AccessTools.InnerTypes(terra).Bounded(MaxNested))
        {
            foreach (var method in AccessTools.GetDeclaredMethods(nested).Bounded(MaxMethods))
            {
                if (!method.Name.StartsWith(Body, StringComparison.Ordinal) || !CallsForColumn(method, forColumn)) continue;
                if (found != null) return null; // two candidates: not the engine this was verified against
                found = method;
            }
        }

        return NotNull(found) && Assert(found.ReturnType == typeof(void)) ? found : null;
    }

    private static bool CallsForColumn(MethodInfo method, MethodInfo forColumn)
    {
        if (!NotNull(method) || !NotNull(forColumn) || method.GetMethodBody() == null) return false;
        foreach (var code in PatchProcessor.GetOriginalInstructions(method).Bounded(MaxInstructions))
            if (code.Calls(forColumn))
                return true;
        return false;
    }

    // Harmony reruns every transpiler of a method whenever a patch is added to or taken from it, so a run after Install means another
    // mod patched it, and what that patch does with a column is not verified here. Harmony runs transpilers under its global lock, so
    // only one run sees _foreign turn true and logs it.
    private static void Rerun(int bit)
    {
        _rewritten &= ~bit;
        if (!Assert(bit is 1 or 2 or 4) || !Volatile.Read(ref _installed) || Volatile.Read(ref _foreign)) return;
        Volatile.Write(ref _foreign, true); // read by the generation threads
        _logger?.Notification(
            "Komet ColumnNoiseScratch stands down: another mod patched {0} after install; terrain columns allocate as shipped",
            bit switch
            {
                4 => "NewNormalizedSimplexFractalNoise.ForColumn",
                1 => "the ColumnNoise constructor",
                _ => "GenTerra's column body"
            });
    }

    // ForColumn is its arguments and newobj ColumnNoise: nothing runs between the arm and the constructor that takes it. Patched only
    // to hear of other patches; the IL goes back unchanged.
    internal static List<CodeInstruction> WatchForColumn(IEnumerable<CodeInstruction> instructions)
    {
        Rerun(4);
        var ctor = Constructor();
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor)) return code;
        var (calls, builds) = (Il.Count(code, c => c.opcode.FlowControl == FlowControl.Call),
            Il.Count(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)));
        if (Assert(calls == 1 && builds == 1)) _rewritten |= 4;
        return code;
    }

    // Four newarr - double, int, OctaveEntry, PastEvaluation in this order - the first two stored to locals, the others to their fields
    // on `this`, and nothing else the constructor does can hand a reference on (Allowed). The constructor takes the arm first thing into
    // a local of its own, and each newarr becomes Rent with that and its number. Anything else returns the engine's IL untouched.
    internal static List<CodeInstruction> RewriteConstructor(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        Rerun(1);
        var (entries, past) =
            (AccessTools.Field(typeof(ColumnNoise), "orderedOctaveEntries"),
                AccessTools.Field(typeof(ColumnNoise), "pastEvaluations"));
        var (take, rent) =
            (AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Take)),
                AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Rent)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(generator) || !NotNull(entries) ||
            !NotNull(past) ||
            !NotNull(take) || !NotNull(rent)) return code;
        if (!Assert(code.Count > 0) || code[0].labels.Count > 0 || code[0].blocks.Count > 0) return code;
        Type?[] expected =
            [typeof(double), typeof(int), entries.FieldType.GetElementType(), past.FieldType.GetElementType()];
        if (!Numbers(expected[2]) || !Numbers(expected[3])) return code;
        var at = new int[Arrays];
        var found = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].opcode == OpCodes.Newarr)
            {
                if (found >= Arrays || !Equals(code[i].operand, expected[found]) ||
                    !Held(code, i, found, entries, past)) return code;
                at[found++] = i;
            }
            else if (found > 2 && code[i].operand is MethodInfo { IsStatic: false })
            {
                return code;
            }
            // `this` holds an array from here on
            else if (!Allowed(code[i]) && !(i > 0 && code[i - 1].opcode == OpCodes.Newarr))
            {
                return code;
            }
        // the store Held checked

        if (!Assert(found == Arrays)) return code;
        var scratch = generator.DeclareLocal(typeof(Array[]));
        for (var j = 0; j < Arrays; j++)
        {
            var i = at[Arrays - 1 - j]; // from the last: the inserts shift only what comes after them
            if (!Index(i, code.Count) || code[i].operand is not Type element) return code;
            (code[i].opcode, code[i].operand) =
                (OpCodes.Ldloc, scratch); // in place: labels and exception blocks stay on it
            code.InsertRange(i + 1,
            [
                new CodeInstruction(OpCodes.Ldc_I4, Arrays - 1 - j),
                new CodeInstruction(OpCodes.Call, rent.MakeGenericMethod(element))
            ]);
        }

        code.InsertRange(0, [new CodeInstruction(OpCodes.Call, take), new CodeInstruction(OpCodes.Stloc, scratch)]);
        _rewritten |= 1;
        return code;
    }

    // The locals' arrays go straight to stloc, the fields' straight to stfld of exactly their field on `this`: ldarg.0; ldloc; newarr; stfld
    private static bool Held(List<CodeInstruction> code, int at, int array, FieldInfo entries, FieldInfo past)
    {
        if (!Index(at + 1, code.Count) || !Index(array, Arrays) || at < 2) return false;
        var next = code[at + 1];
        return array switch
        {
            0 or 1 => next.IsStloc(),
            2 => next.StoresField(entries) && code[at - 2].IsLdarg(0),
            _ => next.StoresField(past) && code[at - 2].IsLdarg(0)
        };
    }

    // What the constructor may do besides its four arrays and their stores: load and compute, keep values in locals, write numbers into
    // arrays and fields, and call what takes and returns numbers. No address of a static, field or element, no store through an
    // address, no store of a reference into an array, field or static, no call that could be handed a reference: nothing through which
    // an array could outlive it.
    private static bool Allowed(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return false;
        var op = code.opcode;
        if (op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch || op == OpCodes.Ret) return true;
        if (op == OpCodes.Stfld) return code.operand is FieldInfo { FieldType.IsPrimitive: true };
        if (op == OpCodes.Stelem || op == OpCodes.Initobj) return code.operand is Type element && Numbers(element);
        if (op == OpCodes.Call) return code.operand is MethodInfo method && Numeric(method);
        var name = op.Name ?? "";
        if (name.StartsWith("stelem.", StringComparison.Ordinal)) return op != OpCodes.Stelem_Ref;
        if (name.StartsWith("ldelem", StringComparison.Ordinal)) return op != OpCodes.Ldelema;
        if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) return false;
        return name.StartsWith("ldarg", StringComparison.Ordinal) ||
               name.StartsWith("ldloc", StringComparison.Ordinal) ||
               name.StartsWith("stloc", StringComparison.Ordinal) ||
               name.StartsWith("ldc.", StringComparison.Ordinal) ||
               name.StartsWith("conv.", StringComparison.Ordinal) ||
               name is "ldfld" or "ldlen" or "ldnull" or "dup" or "pop" or "nop" or "add" or "sub" or "mul" or "div"
                   or "div.un" or "rem" or
                   "rem.un" or "neg" or "and" or "or" or "xor" or "not" or "shl" or "shr" or "shr.un" or "ceq" or "cgt"
                   or "cgt.un" or "clt" or
                   "clt.un";
    }

    // Static, or one of ColumnNoise's bound setters on `this`, and nothing but numbers in or out
    private static bool Numeric(MethodInfo method)
    {
        if (!NotNull(method) || !NotNull(method.DeclaringType)) return false;
        if (!method.IsStatic && !Setter(method)) return false;
        if (method.ReturnType != typeof(void) && !method.ReturnType.IsPrimitive) return false;
        var parameters = method.GetParameters();
        for (var i = 0; i < Math.Min(parameters.Length, MaxParameters); i++)
            if (!parameters[i].ParameterType.IsPrimitive)
                return false;
        return parameters.Length <= MaxParameters;
    }

    // An instance method gets `this`, and with it the arrays once they are stored, so it is taken only when its own IL is an
    // auto-property setter of ColumnNoise: ldarg.0; ldarg.1; stfld of a number of ColumnNoise's; ret. RewriteConstructor also takes one
    // only before the arrays go into `this`, so even a patch another mod puts on a setter sees none of them.
    private static bool Setter(MethodInfo method)
    {
        if (!NotNull(method) || !Assert(!method.IsStatic) || method.DeclaringType != typeof(ColumnNoise) ||
            method.GetMethodBody() == null)
            return false;
        var code = PatchProcessor.GetOriginalInstructions(method);
        return code.Count == 4 && code[0].IsLdarg(0) && code[1].IsLdarg(1) && code[3].opcode == OpCodes.Ret &&
               code[2].opcode == OpCodes.Stfld &&
               code[2].operand is FieldInfo { FieldType.IsPrimitive: true, IsStatic: false } field &&
               field.DeclaringType == typeof(ColumnNoise);
    }

    // A number, or a struct of numbers: nothing in it can refer to an array
    private static bool Numbers(Type? type)
    {
        if (!NotNull(type) || !type.IsValueType) return false;
        if (type.IsPrimitive) return true;
        var fields = AccessTools.GetDeclaredFields(type);
        for (var i = 0; i < Math.Min(fields.Count, MaxFields); i++)
            if (!fields[i].IsStatic && !fields[i].FieldType.IsPrimitive)
                return false;
        return Assert(fields.Count <= MaxFields);
    }

    // Exactly one ForColumn, its column stored to a local that is only ever addressed for ColumnNoise's own members (Member), and no
    // column built any other way; anything else returns the engine's IL untouched
    internal static List<CodeInstruction> RewriteBody(IEnumerable<CodeInstruction> instructions)
    {
        Rerun(2);
        var (forColumn, ctor, mine) = (ForColumn(), Constructor(),
            AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Column)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(forColumn) || !NotNull(ctor) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.Calls(forColumn));
        var built = Il.Count(code,
            c => c.operand is MethodBase method && method == ctor); // a column the arm would not cover
        if (!Assert(at >= 0) || built != 0 || !Index(at + 1, code.Count) || !code[at + 1].IsStloc()) return code;
        if (!Il.Confined(code, at + 1, Member, Il.Uses.Address, MaxReach)) return code;
        if (Assert(Il.Substitute(code, at, mine))) _rewritten |= 2;
        return code;
    }

    // A call of one of ColumnNoise's own double-returning members with the column's address as `this`: the column itself is never
    // copied, stored again or handed on
    private static bool Member(CodeInstruction code, int operand)
    {
        if (!NotNull(code) || !Assert(operand >= 0)) return false;
        return operand == 0 && code.opcode == OpCodes.Call && code.operand is MethodInfo { IsStatic: false } member &&
               member.DeclaringType == typeof(ColumnNoise) && member.ReturnType == typeof(double);
    }

    // Stands in for the body's terrainNoise.ForColumn(...): the same call, with this thread armed for the one constructor it runs.
    // A null noise throws in ForColumn as the engine's callvirt would, and the finally disarms the thread whatever throws.
    internal static ColumnNoise Column(NewNormalizedSimplexFractalNoise noise, double relativeYFrequency,
        double[] amplitudes,
        double[] thresholds, double noiseX, double noiseZ)
    {
        if (!Enabled || Volatile.Read(ref _foreign) || !Assert(!_armed))
            return noise.ForColumn(relativeYFrequency, amplitudes, thresholds, noiseX, noiseZ);
        _armed = true;
        try
        {
            return noise.ForColumn(relativeYFrequency, amplitudes, thresholds, noiseX, noiseZ);
        }
        finally
        {
            _armed = false; // also when no constructor took it: ForColumn threw before it ran one, say
        }
    }

    // First thing in every ColumnNoise constructor: the thread's arrays if this constructor builds from them, else null. Not null at
    // most once per arming.
    private static Array?[]? Take()
    {
        if (!_armed) return null;
        _armed = false;
        var arrays = _arrays ??= new Array?[Arrays * MaxLength];
        return Assert(arrays.Length == Arrays * MaxLength) ? arrays : null;
    }

    // Stands in for the constructor's newarr T, the slot-th of its four: with the thread's arrays, the one of exactly this length for
    // that slot, zeroed as a new one would be; else, and for a length past the table, a new one. A negative length throws as newarr's.
    internal static T[] Rent<T>(int length, Array?[]? arrays, int slot)
    {
        if (arrays is null || (uint)length >= MaxLength || !Index(slot, Arrays)) return new T[length];
        var at = slot * MaxLength + length;
        var cached = arrays[at];
        if (cached?.GetType() !=
            typeof(T[])) // exactly T[], one compare: a cast to int[] would also take a uint[], in a helper call
        {
            var fresh = new T[length];
            arrays[at] = fresh;
            return fresh;
        }

        var array = Unsafe.As<T[]>(cached);
        array.AsSpan().Clear();
        if (Counting.Hud) _ = Interlocked.Add(ref _saved, (ArrayHeader + (long)length * Unsafe.SizeOf<T>() + 7) & ~7L);
        return Assert(array.Length == length) ? array : new T[length];
    }
}
