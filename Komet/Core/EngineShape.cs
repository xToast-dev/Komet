using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace Komet.Core;

// A prefix that answers in place of an engine method reproduces that method's body, so it is only right for the body it was written
// and golden-tested against: after a game update that changes one comparison it would go on computing the old result, silently. The
// fingerprint is of the body as the assembly ships it: every opcode and operand, with the members, types and strings the metadata
// tokens name written out by name (tokens renumber when the assembly is rebuilt, names do not), the local variable types and the
// exception clauses. So it holds across rebuilds that leave the method alone and changes with any instruction that does not. A feature
// compares the fingerprint of the methods it replaces with that of Vintage Story 1.22.7 at install and, on any difference, leaves them
// to the engine (logged once); the golden tests assert the values against the installed engine. It runs once per install, on the
// main thread, so it works with strings. A new replacement pins Of(its bodies) of the installed engine, as EngineShapeTests does.
// The second half of the guard is Foreign: a patch on a seam that the replacement would skip or bypass.
internal static class EngineShape
{
    // Harmony's patch kinds. Body is what changes what the original computes; Replacing adds the prefix that may skip it or change its
    // arguments: what a replacement prefix (which skips the original) has to stand down for.
    [Flags]
    public enum Kinds
    {
        None = 0,
        Prefix = 1,
        Postfix = 2,
        Transpiler = 4,
        Finalizer = 8,
        InnerPrefix = 16,
        InnerPostfix = 32,
        Body = Transpiler | InnerPrefix | InnerPostfix,
        Replacing = Prefix | Body,
        All = Replacing | Postfix | Finalizer
    }

    public const int MaxMethods = 64, MaxSeams = 64;

    private const int MaxIl = 1 << 16, MaxLocals = 256, MaxClauses = 64, MaxCases = 4096, MaxParameters = 64,
        MaxOpCodes = 512;
    private const int TwoByte = 0xFE, MaxPatches = 64, MaxOwn = 8, MaxNameSteps = 4096;
    private const ulong Basis = 14695981039346656037UL, Prime = 1099511628211UL;

    // OpCodes by their byte: the one-byte codes, and the second byte of the 0xFE codes
    private static readonly (OpCode?[] One, OpCode?[] Two) Codes = Table();

    private static (OpCode?[] One, OpCode?[] Two) Table()
    {
        var (one, two) = (new OpCode?[256], new OpCode?[256]);
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Bounded(MaxOpCodes))
        {
            if (field.GetValue(null) is not OpCode code) continue;
            if (code.Size == 1) one[code.Value & 0xFF] = code;
            else two[code.Value & 0xFF] = code;
        }

        _ = Assert(one[OpCodes.Ret.Value] is not null) && Assert(two[OpCodes.Ceq.Value & 0xFF] is not null);
        return (one, two);
    }

    // pinnedBy: the mod that pinned them, for another mod's feature (Komet's own are pinned to Vintage Story 1.22.7).
    public static bool Matches(ReadOnlySpan<MethodBase?> methods, ulong expected, string feature, ILogger? logger,
        string? pinnedBy = null)
    {
        if (!NotNull(feature) || !Assert(methods.Length > 0)) return false;
        var found = Of(methods);
        if (found == expected) return true;
        if (pinnedBy is null)
            logger?.Warning(
                "Komet {0}: the engine methods it replaces are not the Vintage Story 1.22.7 bodies it was tested " +
                "against (fingerprint {1:X16}, expected {2:X16}); the engine keeps running them", feature, found,
                expected);
        else
            logger?.Warning(
                "Komet: {0} is not installed, the engine methods it replaces are not the bodies {1} pinned " +
                "(fingerprint {2:X16}, expected {3:X16}); the engine keeps running them", feature, pinnedBy, found,
                expected);
        return false;
    }

    // Whether a patch of one of the kinds sits on a seam and is not Komet's own - own is one under the Harmony id `owner` (null: none)
    // or declared by one of the `own` classes. A missing seam counts as foreign (not the engine this was verified against), and so
    // do more than MaxPatches patches of one kind. Takes Harmony's lock once per seam.
    public static bool Foreign(ReadOnlySpan<MethodBase?> seams, Kinds kinds, string? owner,
        params ReadOnlySpan<Type> own)
    {
        if (!Assert(seams.Length <= MaxSeams) || !Assert(kinds != Kinds.None) ||
            !Assert(own.Length <= MaxOwn)) return true;
        for (var i = 0; i < Math.Min(seams.Length, MaxSeams); i++)
        {
            if (seams[i] is not { } seam) return true;
            var info = Harmony.GetPatchInfo(seam);
            if (info is null) continue;
            if (Others(info.Prefixes, kinds & Kinds.Prefix, owner, own) ||
                Others(info.Postfixes, kinds & Kinds.Postfix, owner, own) ||
                Others(info.Transpilers, kinds & Kinds.Transpiler, owner, own) ||
                Others(info.Finalizers, kinds & Kinds.Finalizer, owner, own) ||
                Others(info.InnerPrefixes, kinds & Kinds.InnerPrefix, owner, own) ||
                Others(info.InnerPostfixes, kinds & Kinds.InnerPostfix, owner, own)) return true;
        }

        return false;
    }

    // The first seam another mod patches, or that is missing; -1 for none, seams.Length when the check itself fails
    public static int FirstForeign(ReadOnlySpan<MethodBase?> seams, string owner)
    {
        if (!NotNull(owner) || !Assert(seams.Length <= MaxSeams)) return seams.Length;
        for (var i = 0; i < Math.Min(seams.Length, MaxSeams); i++)
            if (Foreign(seams.Slice(i, 1), Kinds.All, owner))
                return i;
        return -1;
    }

    public static bool Report(ILogger? logger, string feature, bool was, bool foreign)
    {
        if (!NotNull(feature) || was == foreign) return foreign;
        if (foreign)
            logger?.Notification(
                "Komet {0}: another mod patches a method it would skip or bypass, the engine's version runs", feature);
        else logger?.Notification("Komet {0}: no other mod patches its methods any more, back on", feature);
        return foreign;
    }

    private static bool Others(ReadOnlyCollection<Patch>? patches, Kinds kind, string? owner, ReadOnlySpan<Type> own)
    {
        if (kind == Kinds.None || patches is null) return false;
        for (var k = 0; k < Math.Min(patches.Count, MaxPatches); k++)
            if (!Own(patches[k], owner, own))
                return true;
        return !Assert(patches.Count <= MaxPatches);
    }

    // Komet's own patches (its mod id as Harmony owner) never count: each gives the engine's result, so no feature steps aside for
    // another (a new seam patch once kept OccludedChunks off for good). The profiler and API features patch under ids of their own.
    private static bool Own(Patch patch, string? owner, ReadOnlySpan<Type> own)
    {
        if (!NotNull(patch)) return false;
        if (patch.owner == KometModSystem.ModId || (owner is not null && patch.owner == owner)) return true;
        var type = patch.PatchMethod?.DeclaringType;
        if (type is null) return false;
        for (var i = 0; i < Math.Min(own.Length, MaxOwn); i++)
            if (type == own[i])
                return true;
        return false;
    }

    public static ulong Of(ReadOnlySpan<MethodBase?> methods)
    {
        var hash = Basis;
        if (!Assert(methods.Length <= MaxMethods)) return 0;
        for (var i = 0; i < Math.Min(methods.Length, MaxMethods); i++)
        {
            var one = Of(methods[i]);
            if (one == 0) return 0;
            hash = Mix(hash, one.ToString(CultureInfo.InvariantCulture));
        }

        return Assert(hash != 0) ? hash : 1;
    }

    public static ulong Of(MethodBase? method)
    {
        if (method is null) return 0;
        try
        {
            // throws for locals of a type whose assembly does not load
            var body = method.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (body is null || il is null || !Assert(il.Length <= MaxIl)) return 0;
            var (locals, clauses) = (body.LocalVariables, body.ExceptionHandlingClauses);
            if (!Assert(locals.Count <= MaxLocals) || !Assert(clauses.Count <= MaxClauses)) return 0;
            var hash = Mix(Mix(Basis, Describe(method)), il.Length.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < Math.Min(locals.Count, MaxLocals); i++) hash = Mix(hash, Name(locals[i].LocalType));
            for (var i = 0; i < Math.Min(clauses.Count, MaxClauses); i++) hash = Clause(hash, clauses[i]);
            hash = Code(method, il, hash);
            return hash == 0 ? 1 : hash;
        }
        catch (Exception e) when (e is ArgumentException or BadImageFormatException or TypeLoadException
                                      or FileNotFoundException or FileLoadException or MissingMemberException
                                      or InvalidOperationException)
        {
            return 0; // a token the runtime cannot resolve: not a body this can vouch for
        }
    }

    private static ulong Code(MethodBase method, byte[] il, ulong hash)
    {
        if (!NotNull(method) || !Assert(il.Length <= MaxIl)) return 0;
        var (at, end) = (0, 0);
        // an instruction takes at least one byte
        for (var n = 0; n < MaxIl && at < il.Length; n++)
        {
            int value = il[at++];
            if (value == TwoByte && at < il.Length) value = (TwoByte << 8) | il[at++];
            var code = value > 0xFF ? Codes.Two[value & 0xFF] : Codes.One[value];
            if (code is not { } op) return 0;
            var size = Size(op.OperandType, il, at);
            if (size < 0 || at + size > il.Length) return 0;
            var name = size == 4 ? Resolve(method, BitConverter.ToInt32(il, at), op.OperandType) : null;
            hash = Mix(Mix(hash, value.ToString(CultureInfo.InvariantCulture)),
                name ?? Convert.ToHexString(il, at, size));
            end = at += size;
        }

        return Assert(end == il.Length) ? hash : 0; // the last instruction ends where the body does
    }

    private static ulong Clause(ulong hash, ExceptionHandlingClause clause)
    {
        if (!NotNull(clause) || !Assert(clause.TryOffset >= 0 && clause.HandlerOffset >= 0)) return hash;
        foreach (var number in (ReadOnlySpan<int>)
                 [(int)clause.Flags, clause.TryOffset, clause.TryLength, clause.HandlerOffset, clause.HandlerLength])
            hash = Mix(hash, number.ToString(CultureInfo.InvariantCulture));
        if (clause.Flags == ExceptionHandlingClauseOptions.Clause) return Mix(hash, Name(clause.CatchType));
        return clause.Flags == ExceptionHandlingClauseOptions.Filter
            ? Mix(hash, clause.FilterOffset.ToString(CultureInfo.InvariantCulture))
            : hash;
    }

    private static int Size(OperandType type, byte[] il, int at)
    {
        if (!Assert(at >= 0) || !NotNull(il)) return -1;
        return type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => at + 4 <= il.Length &&
                                        BitConverter.ToInt32(il, at) is >= 0 and <= MaxCases and var cases
                ? 4 + 4 * cases
                : -1,
            _ => 4
        };
    }

    private static string? Resolve(MethodBase method, int token, OperandType type)
    {
        var module = method.Module;
        if (!NotNull(module) || !Assert(type != OperandType.InlineNone)) return null;
        var generics = method.IsGenericMethod ? method.GetGenericArguments() : null;
        return type switch
        {
            OperandType.InlineString => "\"" + module.ResolveString(token),
            OperandType.InlineSig => Convert.ToHexString(module.ResolveSignature(token)),
            OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType =>
                Describe(module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(), generics)),
            _ => null
        };
    }

    // A member by its declaring type, name and parameter types, written without assembly versions
    private static string Describe(MemberInfo? member)
    {
        if (!NotNull(member)) return "";
        if (member is MethodBase method)
        {
            var parameters = method.GetParameters();
            if (!Assert(parameters.Length <= MaxParameters)) return "";
            return
                $"{Name(method.DeclaringType)}::{method.Name}({string.Join(",", parameters.Select(p => Name(p.ParameterType)))})";
        }

        if (member is FieldInfo field) return $"{Name(field.DeclaringType)}::{field.Name}:{Name(field.FieldType)}";
        return member is Type type ? Name(type) : member.Name;
    }

    private static string Name(Type? type)
    {
        if (type is null) return "";
        var text = new StringBuilder();
        var work = new Stack<(Type? Type, string Separator)>();
        work.Push((type, ""));
        for (var step = 0; step < MaxNameSteps && work.Count > 0; step++)
        {
            var (next, separator) = work.Pop();
            if (next is null)
            {
                _ = text.Append(separator);
                continue;
            }

            var constructed = next.IsGenericType && !next.IsGenericTypeDefinition;
            var arguments = constructed ? next.GetGenericArguments() : [];
            if (!Assert(arguments.Length <= MaxParameters)) continue;
            _ = text.Append(next.Namespace).Append('.').Append(next.Name).Append(constructed ? "<" : "");
            if (constructed) work.Push((null, ">"));
            for (var k = 0; k < Math.Min(arguments.Length, MaxParameters); k++)
            {
                work.Push((arguments[arguments.Length - 1 - k], ""));
                if (k < arguments.Length - 1) work.Push((null, ","));
            }
        }

        return Assert(work.Count == 0) ? text.ToString() : "";
    }

    // FNV-1a over the text, then a separator: "ab" + "c" is not "a" + "bc"
    private static ulong Mix(ulong hash, string text)
    {
        if (!NotNull(text) || !Assert(text.Length <= MaxIl)) return hash;
        for (var i = 0; i < Math.Min(text.Length, MaxIl); i++) hash = (hash ^ text[i]) * Prime;
        return (hash ^ 0x1F) * Prime;
    }
}
