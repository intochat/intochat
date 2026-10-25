using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using IntoChat.OS.Modules.Microsoft.Aspire.Deployment;
using IntoChat.OS.Modules.Microsoft.Aspire.Verification;
using IntoChat.OS.Modules.Microsoft.Orleans;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests;

public sealed class MembershipChangedFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ChangesPublishWithoutPollingAndDeactivationReleasesTheSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var membership = new MembershipFeed();
        await using var brain = await TestBrain.Create().WithModule<AspireModule>()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IClusterMembershipFeed>(membership);
                services.AddSingleton<ICandidateHost>(new FakeCandidateHost());
                services.AddSingleton<IClusterHost>(new FakeClusterHost());
                services.AddSingleton<ICandidateVerifier>(new FakeVerifier());
            }).StartAsync(ct);
        var deployment = brain.Get<IDeployment>(IDeployment.Key(brain.Owner.BrainId));
        await using var changes = await brain.Observe<DeploymentChanged>(deployment, ct);
        await deployment.Read();
        Assert.Equal("Active", Assert.Single((await changes.NextAsync(ct: ct)).Silos).State);
        Assert.Equal(1, membership.Subscribers);
        await membership.Change("Active");
        await membership.Change("ShuttingDown");
        Assert.Equal("ShuttingDown", Assert.Single((await changes.NextAsync(ct: ct)).Silos).State);
        await membership.Unavailable();
        Assert.Empty((await changes.NextAsync(ct: ct)).Silos);
        await membership.Change("Active");
        Assert.Equal("Active", Assert.Single((await changes.NextAsync(ct: ct)).Silos).State);
        await brain.DeactivateAsync(deployment, ct);
        Assert.Equal(0, membership.Subscribers);
        await deployment.Read();
        Assert.Equal(1, membership.Subscribers);
        var retained = await deployment.AsReference<IRetainsSignals>().Retained(nameof(DeploymentChanged), 0);
        Assert.Equal("Active", Assert.Single(Assert.IsType<DeploymentChanged>(Assert.Single(retained).Signal).Silos).State);
    }

    private sealed class MembershipFeed : IClusterMembershipFeed
    {
        private readonly Dictionary<Guid, Func<ClusterSnapshot?, Task>> listeners = [];
        public ClusterSnapshot Current { get; private set; } = new([new("silo", "localhost", "Active", null, "sha")], 1);
        public int Subscribers => listeners.Count;
        public IAsyncDisposable Subscribe(Func<ClusterSnapshot?, Task> changed)
        {
            var id = Guid.NewGuid();
            listeners.Add(id, changed);
            return new Subscription(() => listeners.Remove(id));
        }
        public async Task Change(string status)
        {
            Current = Current with { Silos = [Current.Silos[0] with { Status = status }], Version = Current.Version + 1 };
            foreach (var listener in listeners.Values.ToArray()) { await listener(Current); }
        }
        public async Task Unavailable()
        {
            foreach (var listener in listeners.Values.ToArray()) { await listener(null); }
        }
        private sealed class Subscription(Action dispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
    }
}
