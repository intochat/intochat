using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime.Placement;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration.Placement;

public sealed class BrainAffinePlacementFacts
{
    [Fact]
    public async Task TheSameBrainAlwaysMapsToTheSameSilo()
    {
        SiloAddress[] silos = [Silo(11111), Silo(11112), Silo(11113)];
        var director = new BrainAffinePlacementDirector();
        var forward = new Silos(silos);
        var reversed = new Silos([.. silos.Reverse()]);

        var picks = new HashSet<SiloAddress>();
        foreach (var brain in Enumerable.Range(0, 64).Select(i => "brain-" + i))
        {
            var first = await director.OnAddActivation(new BrainAffinePlacement(), Target(brain + "/inbox"), forward);
            Assert.Equal(first, await director.OnAddActivation(new BrainAffinePlacement(), Target(brain + "/vault"), reversed));
            Assert.Equal(first, await director.OnAddActivation(new BrainAffinePlacement(), Target(brain), forward));
            picks.Add(first);
        }
        Assert.Equal(silos.Length, picks.Count);
    }

    private static SiloAddress Silo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 1);

    private static PlacementTarget Target(string key)
        => new(GrainId.Create("test.neuron", key), new Dictionary<string, object>(), GrainInterfaceType.Create("test.neuron"), 0);

    private sealed class Silos(SiloAddress[] silos) : IPlacementContext
    {
        public SiloAddress LocalSilo => silos[0];
        public SiloStatus LocalSiloStatus => SiloStatus.Active;
        public SiloAddress[] GetCompatibleSilos(PlacementTarget target) => silos;
        public IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target) => new Dictionary<ushort, SiloAddress[]> { [0] = silos };
    }
}
