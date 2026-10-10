using System.Net;
using DigitalBrain.Microsoft.Aspire.Candidates;
using DigitalBrain.Microsoft.Aspire.Cluster;
using DigitalBrain.Microsoft.Aspire.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Microsoft.Aspire.Tests;

public sealed class ClusterChangedFacts
{
    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(40);

    private static Task<ModuleBrain> StartAsync(FakeClusterHost cluster, CancellationToken ct, bool http = false)
    {
        var builder = ModuleTest.Create().WithModule<AspireModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddSingleton<ICandidateHost>(new FakeCandidateHost());
                silo.Services.AddSingleton<IClusterHost>(cluster);
                silo.Services.AddSingleton<ICandidateVerifier>(new FakeVerifier());
                silo.Services.Configure<AspireOptions>(options => options.ClusterSampling = Sampling);
            });
        return (http ? builder.WithHttpEdge() : builder).StartAsync(ct);
    }

    private static SiloView[] Silos(string state, params string[] ids) => [.. ids.Select(id => new SiloView(id, "sha0001", state, "Healthy"))];

    [Fact]
    public async Task TwoDifferentSiloListsPublishTwoClusterChangedWithTheSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Reported = Silos("Running", "silo-0") };
        await using var brain = await StartAsync(cluster, ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        await grain.Read();
        var first = await changes.NextAsync(change => change.Silos.Length == 1, ct);
        cluster.Reported = Silos("Running", "silo-0", "silo-1");
        var second = await changes.NextAsync(ct: ct);

        Assert.Equal(("Steady", "base000"), (first.Stage, first.Sha));
        Assert.Equal(["silo-0:Running:Healthy"], first.Silos.Select(Describe));
        Assert.Equal(["silo-0:Running:Healthy", "silo-1:Running:Healthy"], second.Silos.Select(Describe));
    }

    [Fact]
    public async Task AnUnchangedSiloListPublishesNothingMore()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Reported = Silos("Running", "silo-0") };
        await using var brain = await StartAsync(cluster, ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        await grain.Read();
        await changes.NextAsync(ct: ct);
        // Several samples of the same list pass before the list changes; the next signal is the change, not a repeat.
        await WaitSamples(cluster, 4, ct);
        cluster.Reported = Silos("Exited", "silo-0");
        var next = await changes.NextAsync(ct: ct);

        Assert.Equal(["silo-0:Exited:Healthy"], next.Silos.Select(Describe));
    }

    [Fact]
    public async Task ASampleTakenWhileARollIsInProgressPublishesTheChangedSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Self = "silo-0", Reported = Silos("Running", "silo-0", "silo-1") };
        var gate = cluster.RestartGates["mcp-b"] = new RestartGate();
        await using var brain = await StartAsync(cluster, ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);

        await grain.Roll("sha0001");
        await gate.Reached.Task.WaitAsync(ct);
        // The step holds the grain here; the sample still runs and shows the silo that is restarting.
        cluster.Reported = [new("silo-0", "sha0001", "Running", "Healthy"), new("silo-1", "sha0001", "Starting", "")];
        var seen = await changes.NextAsync(change => change.Stage == nameof(ClusterStage.Rolling) && change.Silos.Any(silo => silo.State == "Starting"), ct);
        gate.Open.SetResult();

        Assert.Contains("silo-1:Starting:", seen.Silos.Select(Describe));
    }

    [Fact]
    public async Task AnUnreadableAppHostEmptiesTheSilosOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Reported = Silos("Running", "silo-0") };
        await using var brain = await StartAsync(cluster, ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var changes = await brain.Observe<ClusterChanged>(grain, ct);
        await grain.Read();
        await changes.NextAsync(change => change.Silos.Length == 1, ct);

        cluster.SilosFail = true;
        var empty = await changes.NextAsync(ct: ct);
        await WaitSamples(cluster, 4, ct);
        cluster.SilosFail = false;
        var back = await changes.NextAsync(ct: ct);

        Assert.Empty(empty.Silos);
        Assert.Equal(["silo-0:Running:Healthy"], back.Silos.Select(Describe));
    }

    [Fact]
    public async Task ALateSubscriberIsSeededWithTheLastClusterChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = new FakeClusterHost { Reported = Silos("Running", "silo-0") };
        await using var brain = await StartAsync(cluster, ct);
        var grain = brain.Get<ICluster>(ICluster.Key("owner"));
        await using var early = await brain.Observe<ClusterChanged>(grain, ct);
        await grain.Read();
        await early.NextAsync(ct: ct);

        var retained = await brain.Get<IRetainsSignals>(ICluster.Key("owner")).Retained(nameof(ClusterChanged), 0);

        var last = Assert.IsType<ClusterChanged>(Assert.Single(retained).Signal);
        Assert.Equal(["silo-0:Running:Healthy"], last.Silos.Select(Describe));
    }

    [Fact]
    public async Task TheInfrastructureRouteNoLongerExists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(new FakeClusterHost(), ct, http: true);

        var response = await brain.HttpClient.GetAsync("/brains/ws-infra/infrastructure", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Describe(SiloState silo) => $"{silo.Id}:{silo.State}:{silo.Health}";

    private static async Task WaitSamples(FakeClusterHost cluster, int count, CancellationToken ct)
    {
        while (cluster.SiloRead.CurrentCount > 0) { await cluster.SiloRead.WaitAsync(ct); }
        for (var i = 0; i < count; i++) { await cluster.SiloRead.WaitAsync(ct); }
    }
}
