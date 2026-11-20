using DigitalBrain.Capabilities.Connections.Connectors;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Modules.Identity.Integrations;

// A module declares what it needs with `public static IntegrationSchema Integration` or
// `public static IntegrationSchema[] Integrations`; a module with neither contributes nothing.
internal static class IntegrationDiscovery
{
    public static IReadOnlyList<IntegrationSchema> Collect(IEnumerable<Type> moduleTypes)
    {
        var found = new List<IntegrationSchema>();
        foreach (var module in moduleTypes.Distinct())
        {
            found.AddRange(Declared(module));
        }

        var duplicate = found.GroupBy(definition => definition.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Integration '{duplicate.Key}' is declared by more than one module.");
        }

        return found;
    }

    internal static IEnumerable<IntegrationSchema> Declared(Type module)
        => OsModule.ReadSchemas(module);
}
