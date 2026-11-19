using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace Komet.Sdk;

// The Engine view's index of the player's own game assemblies (VintagestoryLib, VintagestoryAPI, VSEssentials,
// VSSurvivalMod from the installation, never shipped): one background pass over every method's IL records which methods
// load a string literal (a shader program's name, a uniform's name) and which methods call which ("Type::method",
// simple type names). The engine names a program by its literal ("chunkopaque") or its ShaderPrograms getter, and sets a
// uniform by its literal, mostly inside a generated property setter of ShaderProgram<Name> whose callers are the real
// sites. Queries pick those methods and decompile only them (EngineSource.Query). Thread: Start on the main thread, the
// index on a worker; queries read it after Ready.
internal static partial class EngineSource
{
    private const int MaxMethods = 1 << 18, MaxIl = 1 << 17, MaxSites = 64, MaxLiteral = 48;

    private static readonly string[] Names = ["VintagestoryLib", "VintagestoryAPI", "VSEssentials", "VSSurvivalMod"];
    private static readonly Dictionary<string, List<Site>> Strings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Site>> Calls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Paths = [];
    private static volatile int _state; // 0 not started, 1 indexing, 2 ready, 3 failed

    public static string Error { get; private set; } = "";
    public static bool Ready => _state == 2;
    public static bool Failed => _state == 3;

    // The first Engine view starts the index; later calls do nothing
    public static void Start()
    {
        if (_state != 0) return;

        _state = 1;
        Paths.AddRange(Locate());
        _ = Task.Run(Index);
    }

    // The game's own copies: those the process loaded, else the installation's files
    private static List<string> Locate()
    {
        List<string> found = [];
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var name in Names.Bounded(8))
        {
            var path = Array.Find(loaded, a => !a.IsDynamic && a.GetName().Name == name)?.Location;
            path = string.IsNullOrEmpty(path) ? Installed(name) : path;
            if (path is not null && File.Exists(path)) found.Add(path);
        }

        return found;
    }

    private static string? Installed(string name)
    {
        foreach (var folder in (string[])[GamePaths.Binaries, Path.Join(GamePaths.Binaries, "Mods")])
        {
            var path = Path.Join(folder, name + ".dll");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static void Index()
    {
        try
        {
            for (var i = 0; i < Math.Min(Paths.Count, Names.Length); i++)
            {
                using var file = new PEFile(Paths[i]);
                IndexFile(file, i);
            }

            _state = Paths.Count > 0 ? 2 : 3;
            Error = Paths.Count > 0 ? "" : "the game's assemblies were not found";
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or
                                      UnauthorizedAccessException)
        {
            Error = e.Message;
            _state = 3;
        }
    }

    private static void IndexFile(PEFile file, int assembly)
    {
        var metadata = file.Metadata;
        var methods = metadata.MethodDefinitions.ToArray();
        foreach (var handle in methods.Bounded(MaxMethods))
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;

            var reader = file.Reader.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            Scan(metadata, ref reader, new Site(assembly, handle));
        }
    }

    // One method's IL: its string literals and its calls
    private static void Scan(MetadataReader metadata, ref BlobReader reader, Site site)
    {
        for (var i = 0; i < MaxIl && reader.RemainingBytes > 0; i++)
        {
            var op = reader.DecodeOpCode();
            if (op == ILOpCode.Ldstr)
            {
                var text = metadata.GetUserString(MetadataTokens.UserStringHandle(reader.ReadInt32()));
                if (Literal(text)) Add(Strings, text, site);
            }
            else if (op is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj)
            {
                if (Key(metadata, MetadataTokens.EntityHandle(reader.ReadInt32())) is { } key) Add(Calls, key, site);
            }
            else
            {
                reader.SkipOperand(op);
            }
        }
    }

    private static bool Literal(string text) =>
        text.Length is >= 2 and <= MaxLiteral && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static void Add(Dictionary<string, List<Site>> index, string key, Site site)
    {
        if (!index.TryGetValue(key, out var sites)) index[key] = sites = [];

        if (sites.Count < MaxSites && (sites.Count == 0 || sites[^1] != site)) sites.Add(site);
    }

    // "Type::method" of a call's target (simple type name), null for what is no method
    private static string? Key(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                return metadata.GetString(type.Name) + "::" + metadata.GetString(method.Name);
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return TypeName(metadata, member.Parent) + "::" + metadata.GetString(member.Name);
            default:
                return null;
        }
    }

    private static string TypeName(MetadataReader metadata, EntityHandle parent) =>
        parent.Kind switch
        {
            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)parent)
                .Name),
            _ => "?"
        };

    // A method of one of the indexed assemblies
    private readonly record struct Site(int Assembly, MethodDefinitionHandle Method);
}
