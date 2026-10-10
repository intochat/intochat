using DigitalBrain.Framework;
using global::Orleans.Runtime;

namespace DigitalBrain.Microsoft.Orleans.Cluster;

[GrainType("orleans.cluster")]
internal sealed class ClusterNeuron(IGrainFactory grains) : Neuron, ICluster, IRetainsSignals
{
    private ClusterSnapshot? _last;

    public async Task<ClusterSnapshot> Read()
    {
        var hosts = await grains.GetGrain<IManagementGrain>(0).GetHosts(onlyActive: false);
        var silos = hosts.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => new SiloView(pair.Key.ToString(), pair.Key.Endpoint.ToString(), pair.Value.ToString(), null, ThisAssembly.Sha))
            .ToArray();
        var next = new ClusterSnapshot(silos, (_last?.Version ?? 0) + 1);
        if (_last is null || !_last.Silos.SequenceEqual(next.Silos)) { _last = next; await PublishAsync(new ClusterChanged(next)); }
        return _last;
    }

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(ClusterChanged) && _last is not null && _last.Version > since
            ? [new RetainedSignal(_last.Version, new ClusterChanged(_last) { Publisher = this.GetPrimaryKeyString() })] : []);
}

internal static class ThisAssembly
{
    public static readonly string Sha = typeof(ThisAssembly).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault() ?? "source";
}
