using DigitalBrain;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

[GenerateSerializer]
internal sealed record InstalledAppsState
{
    [Id(0)] public PackageId[] Packages { get; init; } = [];
    // IDs 1 (Discovered) and 2 (DiscoveryOffset) are retired; never reuse.
}

[GrainType("apps.installed")]
internal sealed class Apps(
    [PersistentState("installed-apps", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<InstalledAppsState> store)
    : Neuron<InstalledAppsState>(store), IApps
{
    public override DigitalBrain.Framework.Enforcement.NeuronAccess Access(string operation) => new(this.GetPrimaryKeyString());

    public Task<PackageId[]> List() => Task.FromResult(Snapshot.Packages.ToArray());

    public Task<InstalledAppsPage> Page(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 21);
        return Task.FromResult(new InstalledAppsPage(Snapshot.Packages.OrderBy(id => id.ToString(), StringComparer.Ordinal).Skip(offset).Take(limit).ToArray()));
    }

    public async Task Track(PackageId package)
    {
        package = PackageId.Create(package.Owner, package.Name);
        if (!Snapshot.Packages.Contains(package))
        {
            var next = Snapshot with { Packages = [.. Snapshot.Packages, package] };
            await Save(next, new AppsChanged(next.Packages));
        }
    }
}
