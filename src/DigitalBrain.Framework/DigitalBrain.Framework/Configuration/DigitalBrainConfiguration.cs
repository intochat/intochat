using System.Text.Json;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Framework.Configuration;

// Configuration is the system's one value channel, and this is its grammar: a key path is
// ':'-separated, its environment form swaps ':' for '__', and an options object flattens to
// leaf keys under its section. Every ring speaks the channel through these three; no other
// file implements the conventions.
public static class DigitalBrainConfiguration
{
    // The module composition's configuration home: enablement flags and per-module options
    // live under these sections, and a brain-scoped overlay may restate options per brain.
    public const string ModulesSectionName = "DigitalBrain:Modules";

    public const string WorkshopSectionName = "DigitalBrain:Workshop";

    public static string ModuleSection(ModuleId moduleId) => $"{ModulesSectionName}:{moduleId}";

    public static string ModuleSection(string moduleId) => ModuleSection(new ModuleId(moduleId));

    public static string ModuleOptionsSection(ModuleId moduleId) => ModuleSection(moduleId) + ":Options";

    public static string ModuleOptionsSection(string moduleId) => ModuleOptionsSection(new ModuleId(moduleId));

    public static string CompositionModuleOptionsSection(string compositionName, ModuleId moduleId)
        => $"DigitalBrain:Compositions:{compositionName}:Modules:{moduleId}:Options";

    public static string CompositionModuleOptionsSection(string compositionName, string moduleId)
        => CompositionModuleOptionsSection(compositionName, new ModuleId(moduleId));

    // The Platform's connection flow: where providers send browsers back, and per-connector token
    // endpoint overrides for test hosts.
    public const string ConnectionsSectionName = "DigitalBrain:Connections";

    public const string ConnectionsPublicOriginKey = ConnectionsSectionName + ":PublicOrigin";

    public static string ConnectionsTokenEndpointKey(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        return $"{ConnectionsSectionName}:TokenEndpoints:{connectorId}";
    }

    public static string EnvironmentName(string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        return configurationKey.Replace(":", "__", StringComparison.Ordinal);
    }

    public static string EnvironmentName(string configurationRoot, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return EnvironmentName($"{configurationRoot}:{name}");
    }

    // An Integration field's environment name, for a deployer composing another process's environment
    // (a candidate silo's own, freshly generated values). Reading a deployment's credentials still goes
    // through IModuleIntegrations; this only names the variable.
    public static string IntegrationEnvironmentName(string integrationId, string field)
        => EnvironmentName(IntegrationSchema.Key(integrationId, field));

    // omitDefaults: a cross-process projection sends only values the sender decided, so a leaf
    // equal to the type's default cannot pin the receiver's own configuration. An in-process
    // composition keeps full fidelity.
    public static Dictionary<string, string?> Flatten(object options, string section, bool omitDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        var declared = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(JsonSerializer.SerializeToElement(options), section, declared);
        if (!omitDefaults) { return declared; }
        var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(JsonSerializer.SerializeToElement(Activator.CreateInstance(options.GetType())!), section, defaults);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in declared)
        {
            if (!defaults.TryGetValue(pair.Key, out var fallback) || fallback != pair.Value) { values[pair.Key] = pair.Value; }
        }
        return values;

        static void Collect(JsonElement element, string path, Dictionary<string, string?> into)
        {
            if (element.ValueKind == JsonValueKind.Object)
            { foreach (var property in element.EnumerateObject()) { Collect(property.Value, path + ":" + property.Name, into); } }
            else if (element.ValueKind == JsonValueKind.Array)
            { var index = 0; foreach (var value in element.EnumerateArray()) { Collect(value, path + ":" + index++, into); } }
            else { into[path] = element.ValueKind == JsonValueKind.Null ? null : element.ToString(); }
        }
    }
}
