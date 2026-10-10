namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("microsoft.aspire.infrastructure-row")]
public sealed record InfrastructureRow(
    [property: Id(0)] string Module,
    [property: Id(1)] string Resource,
    [property: Id(2)] string State,
    [property: Id(3)] string? Health);

// The aspire module's rows are the cluster's silos; with none reported the AppHost's state is unknown.
public static class InfrastructureView
{
    public static IReadOnlyList<InfrastructureRow> Join(IEnumerable<string> moduleIds, IReadOnlyList<SiloView> silos)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        ArgumentNullException.ThrowIfNull(silos);
        var rows = new List<InfrastructureRow>();
        foreach (var module in moduleIds.Order(StringComparer.Ordinal))
        {
            if (module != "aspire") { continue; }
            if (silos.Count == 0) { rows.Add(new(module, "silos", "Unknown", null)); continue; }
            foreach (var silo in silos.OrderBy(silo => silo.Name, StringComparer.Ordinal))
            { rows.Add(new(module, silo.Name, silo.State, silo.Health)); }
        }
        return rows;
    }
}
