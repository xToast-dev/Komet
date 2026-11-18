using System.Collections;
using HarmonyLib;
using Komet.Core;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Komet.Options;

// Mods whose settings ConfigLib manages, as pages in the Mods tab: one per mod, its client settings and its server
// settings apart. A switch for a yes/no, a slider for a number with bounds, a choice for a list of values; any other
// setting, and a mod that draws its own ImGui, is left to ConfigLib's window. Through reflection, since ConfigLib is an
// optional mod with no reference at build time: anything not found shows no page and one log line, never an error in
// the menu. A change goes through ConfigLib as its own window makes it (the value, then the setting's Changed, which
// tells the mod and in multiplayer the server); Apply writes the file. Server settings in multiplayer only with the
// controlserver privilege, as in ConfigLib.
internal static class ConfigLibPages
{
    private const string ModId = "configlib", SystemType = "ConfigLib.ConfigLibModSystem", PagePrefix = "configlib-";
    private const int MaxDomains = 64, MaxSettings = 256, SliderSteps = 100;

    private static readonly List<string> Shown = [];
    private static readonly HashSet<object> Changed = []; // configs to write at Apply

    private static ICoreClientAPI? _api;
    private static bool _told;

    public static void Start(ICoreClientAPI api)
    {
        Stop();
        if (!NotNull(api.ModLoader) || !api.ModLoader.IsModEnabled(ModId) || api.Event is null) return;

        _api = api;
        api.Event.LevelFinalize += Build;
        OptionPages.Applied += Save;
    }

    public static void Stop()
    {
        foreach (var id in Shown.Bounded(MaxDomains))
        {
            OptionPages.Remove(id);
        }

        Shown.Clear();
        Changed.Clear();
        OptionPages.Applied -= Save;
        if (_api?.Event is { } events) events.LevelFinalize -= Build;

        _api = null;
    }

    private static void Build()
    {
        if (_api is not { } api) return;

        try
        {
            var system = api.ModLoader.GetModSystem(SystemType);
            if (Get(system, "Domains") is not IEnumerable<string> domains) throw new MissingMemberException("Domains");

            var allowed = api.IsSinglePlayer || api.World?.Player?.HasPrivilege(Privilege.controlserver) == true;
            foreach (var domain in domains.Bounded(MaxDomains))
            {
                Page(api, domain, system?.GetType().GetMethod("GetConfig", [typeof(string)])?.Invoke(system, [domain]),
                    allowed);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Tell(api, e.Message);
        }
    }

    // One mod's page: its client settings, then its server settings; none when nothing is shown as a row
    private static void Page(ICoreClientAPI api, string domain, object? config, bool allowed)
    {
        if (config is null) return;

        var name = Get(config, "ModName") is string { Length: > 0 } modName ? modName : domain;
        var id = PagePrefix + domain;
        var rows = Fill(OptionPages.Page(id, name), config, allowed);
        if (rows == 0)
        {
            OptionPages.Remove(id);
            return;
        }

        Shown.Add(id);
        api.Logger?.Debug("Komet: ConfigLib settings of {0} on its page ({1} rows)", domain, rows);
    }

    // The page's rows from a config, its client settings first, then its server settings, dimmed without the
    // privilege. Returns how many settings got a row.
    internal static int Fill(OptionPage page, object config, bool allowed)
    {
        if (Get(config, "Settings") is not IDictionary settings) return 0;

        var all = settings.Values.Cast<object>().Bounded(MaxSettings)
            .Where(s => Get(s, "Hide") is not true)
            .OrderBy(s => Get(s, "SortingWeight") is float weight ? weight : 0).ToArray();
        var rows = 0;
        foreach (var client in new[] { true, false })
        {
            var side = Array.FindAll(all, s => Get(s, "ClientSide") is bool c && c == client);
            if (side.Length == 0) continue;

            _ = page.Group(T(client ? "configlib-client" : "configlib-server"));
            foreach (var setting in side.Bounded(MaxSettings))
            {
                if (Row(page, setting, config, client || allowed)) rows++;
            }
        }

        return rows;
    }

    // A row for a setting Komet can draw; false for the rest
    private static bool Row(OptionPage page, object setting, object config, bool enabled)
    {
        var label = Get(setting, "InGui") as string is { Length: > 0 } inGui ? inGui : Get(setting, "YamlCode") as string;
        if (label is not { Length: > 0 } || Get(setting, "Value") is not JsonObject) return false;

        var hint = Get(setting, "Comment") as string;
        var validation = Get(setting, "Validation");
        bool made;
        if (Get(validation, "Mapping") is IDictionary mapping)
        {
            made = Mapped(page, (label, hint), setting, config, mapping);
        }
        else if (Get(validation, "Values") is IList values)
        {
            made = Listed(page, (label, hint), setting, config, values);
        }
        else
        {
            made = Plain(page, (label, hint), setting, config, validation);
        }

        if (made && !enabled) _ = page.EnabledWhen(static () => false); // a server setting without controlserver

        return made;
    }

    private static bool Plain(OptionPage page, (string Label, string? Hint) row, object setting, object config,
        object? validation)
    {
        var type = Get(setting, "SettingType")?.ToString();
        if (type == "Boolean")
        {
            _ = page.Switch(row.Label, () => Value(setting).AsBool(), on => Set(setting, config, new JValue(on)),
                row.Hint).Default(Get(setting, "DefaultValue") is JsonObject d && d.AsBool() ? 1 : 0);
            return true;
        }

        if (type is not ("Integer" or "Float") || Get(validation, "Minimum") is not JsonObject min ||
            Get(validation, "Maximum") is not JsonObject max || max.AsDouble() <= min.AsDouble())
        {
            return false;
        }

        var whole = type == "Integer";
        var step = whole ? 1 : (max.AsDouble() - min.AsDouble()) / SliderSteps;
        if (Get(validation, "Step") is JsonObject s && s.AsDouble() > 0) step = s.AsDouble();
        _ = page.Slider(row.Label, min.AsDouble(), max.AsDouble(), step, () => Value(setting).AsDouble(),
            v => Set(setting, config, whole ? new JValue((long)Math.Round(v)) : new JValue(v)), "", row.Hint);
        if (Get(setting, "DefaultValue") is JsonObject standard) _ = page.Default(standard.AsDouble());

        return true;
    }

    // A setting with named values: the player picks a name, the setting takes its value and remembers the name
    private static bool Mapped(OptionPage page, (string Label, string? Hint) row, object setting, object config,
        IDictionary mapping)
    {
        var keys = mapping.Keys.Cast<object>().Select(k => k.ToString() ?? "").ToArray();
        if (keys.Length == 0) return false;

        _ = page.Choice(row.Label, keys, () => Math.Max(0, Array.IndexOf(keys, Get(setting, "MappingKey") as string)),
            i =>
            {
                AccessTools.Property(setting.GetType(), "MappingKey")?.SetValue(setting, keys[i]);
                if (mapping[keys[i]] is JsonObject { Token: { } value }) Set(setting, config, value);
            }, row.Hint);
        return true;
    }

    private static bool Listed(OptionPage page, (string Label, string? Hint) row, object setting, object config,
        IList values)
    {
        var options = values.Cast<object>().OfType<JsonObject>().ToArray();
        if (options.Length == 0) return false;

        var names = Array.ConvertAll(options, o => o.Token?.ToString() ?? "");
        _ = page.Choice(row.Label, names,
            () => Math.Max(0, Array.IndexOf(names, Value(setting).Token?.ToString())),
            i => Set(setting, config, options[i].Token ?? JValue.CreateNull()), row.Hint);
        return true;
    }

    private static JsonObject Value(object setting) =>
        Get(setting, "Value") as JsonObject ?? new JsonObject(JValue.CreateNull());

    // As ConfigLib's window: the value, then Changed (the mod hears it, a server setting goes to the server)
    private static void Set(object setting, object config, JToken token)
    {
        try
        {
            AccessTools.Property(setting.GetType(), "Value")?.SetValue(setting, new JsonObject(token));
            _ = AccessTools.Method(setting.GetType(), "Changed", Type.EmptyTypes)?.Invoke(setting, null);
            _ = Changed.Add(config);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Tell(_api, e.Message);
        }
    }

    // Apply: each changed mod's file written, as ConfigLib's Save does
    internal static void Save(object? sender, EventArgs args)
    {
        foreach (var config in Changed.Bounded(MaxDomains))
        {
            try
            {
                _ = config.GetType().GetMethod("WriteToFile", Type.EmptyTypes)?.Invoke(config, null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Tell(_api, e.Message);
            }
        }

        Changed.Clear();
    }

    private static object? Get(object? target, string name) =>
        target is null ? null : AccessTools.Property(target.GetType(), name)?.GetValue(target);

    private static void Tell(ICoreClientAPI? api, string why)
    {
        if (_told) return;

        _told = true;
        api?.Logger?.Warning("Komet: ConfigLib's settings are not shown in Komet's menu ({0}); ConfigLib's own " +
                             "window still has them", why);
    }

    private static string T(string key) => KometText.Translate("settings-" + key);
}
