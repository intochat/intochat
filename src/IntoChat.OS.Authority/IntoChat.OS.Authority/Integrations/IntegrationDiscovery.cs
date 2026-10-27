using System.Reflection;
using IntoChat.OS.Framework.Modules.Platform.Integrations;

namespace IntoChat.OS.Authority.Integrations;

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
        => Declared(module, "Integration").Concat(Declared(module, "Integrations"));

    private static IEnumerable<IntegrationSchema> Declared(Type module, string member)
    {
        const BindingFlags Static = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var value = module.GetProperty(member, Static)?.GetValue(null) ?? module.GetField(member, Static)?.GetValue(null);
        return value switch
        {
            IntegrationSchema single => [single],
            IEnumerable<IntegrationSchema> many => many,
            _ => [],
        };
    }
}
