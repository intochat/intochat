namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("microsoft.aspire.infrastructure-row")]
public sealed record InfrastructureRow(
    [property: Id(0)] string Module,
    [property: Id(1)] string Resource,
    [property: Id(2)] string State,
    [property: Id(3)] string? Health);

// A module owns a resource when its id is a dash-separated segment of the resource's name
// ("Modules-postgres-server" belongs to "postgres": DigitalBrainBuilder.ResourceName joins with
// dashes). The aspire module's row is the AppHost itself: it has reported, or nothing is known.
public static class InfrastructureView
{
    public static IReadOnlyList<InfrastructureRow> Join(IEnumerable<string> moduleIds, IReadOnlyList<AspireResource> resources)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        ArgumentNullException.ThrowIfNull(resources);
        var rows = new List<InfrastructureRow>();
        foreach (var module in moduleIds.Order(StringComparer.Ordinal))
        {
            if (module == "aspire")
            {
                rows.Add(resources.Count > 0 ? new(module, "apphost", "Running", "Healthy") : new(module, "apphost", "Unknown", null));
                continue;
            }
            foreach (var resource in resources
                .Where(resource => resource.Name.Split('-').Contains(module, StringComparer.OrdinalIgnoreCase))
                .OrderBy(resource => resource.Name, StringComparer.Ordinal))
            { rows.Add(new(module, resource.Name, resource.State, resource.Health)); }
        }
        return rows;
    }
}
