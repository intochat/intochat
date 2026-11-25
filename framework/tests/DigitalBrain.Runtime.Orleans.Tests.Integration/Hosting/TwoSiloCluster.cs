using System.Net;
using System.Net.Sockets;
using DigitalBrain.Runtime.Orleans.Placement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

// Two in-process silos of one cluster on loopback. Start returns once each silo sees both silos active and holds both silos'
// grain manifests: placement chooses among the silos of the local manifest, so before that a silo places on itself alone.
internal sealed class TwoSiloCluster : IAsyncDisposable
{
    // Membership and manifest gossip between two in-process silos; generous because a loaded CI runner is slow, and nothing
    // here measures how fast a silo joins.
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(60);

    private TwoSiloCluster(IHost first, IHost second)
    {
        First = first;
        Second = second;
    }

    public IHost First { get; }

    public IHost Second { get; }

    public static async Task<TwoSiloCluster> Start(string clusterId, Action<ISiloBuilder> configure, CancellationToken ct)
    {
        var primaryPort = FreePort();
        var primary = new IPEndPoint(IPAddress.Loopback, primaryPort);
        var first = Build(primaryPort, primary, clusterId, configure);
        await first.StartAsync(ct);
        var second = Build(FreePort(), primary, clusterId, configure);
        var cluster = new TwoSiloCluster(first, second);
        try
        {
            await second.StartAsync(ct);
            await cluster.UntilJoined(ct);
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    // The silo the brain-affine director picks for a brain: the same rendezvous hash over the two active silos.
    public string SiloOf(string brain)
        => Silos().MaxBy(silo => BrainAffinePlacementDirector.ComputeWeight(brain, silo.ToParsableString()))!.ToString();

    // Brains named prefix0, prefix1, ... until each silo is the affine silo of at least perSilo of them.
    public IReadOnlyList<string> BrainsOnBothSilos(string prefix, int perSilo)
    {
        var silos = Silos().Select(silo => silo.ToString()).ToArray();
        var chosen = new List<string>();
        var counts = silos.ToDictionary(silo => silo, _ => 0);
        for (var i = 0; counts.Values.Any(count => count < perSilo); i++)
        {
            var brain = prefix + i;
            var silo = SiloOf(brain);
            if (counts[silo] < perSilo)
            {
                counts[silo]++;
                chosen.Add(brain);
            }
        }

        return chosen;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Second.StopAsync(CancellationToken.None);
            await First.StopAsync(CancellationToken.None);
        }
        finally
        {
            Second.Dispose();
            First.Dispose();
        }
    }

    private SiloAddress[] Silos()
        => [.. First.Services.GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members.Values
            .Where(member => member.Status == SiloStatus.Active)
            .Select(member => member.SiloAddress)];

    private async Task UntilJoined(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(JoinBudget);
        while (!(Joined(First) && Joined(Second)))
        {
            await Task.Delay(50, limit.Token);
        }
    }

    private static bool Joined(IHost silo)
    {
        var active = silo.Services.GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members.Values
            .Where(member => member.Status == SiloStatus.Active)
            .Select(member => member.SiloAddress)
            .ToHashSet();
        var manifest = silo.Services.GetRequiredService<IClusterManifestProvider>().Current.Silos.Keys;
        return active.Count == 2 && active.All(manifest.Contains);
    }

    private static IHost Build(int siloPort, IPEndPoint primary, string clusterId, Action<ISiloBuilder> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.UseOrleans(orleans =>
        {
            orleans.UseLocalhostClustering(siloPort, FreePort(), primary, clusterId, clusterId);
            orleans.AddPlacementDirector<BrainAffinePlacement, BrainAffinePlacementDirector>();
            configure(orleans);
        });
        return builder.Build();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
