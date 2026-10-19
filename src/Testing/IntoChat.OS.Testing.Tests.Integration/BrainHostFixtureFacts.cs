using System.Net;
using System.Net.Sockets;
using IntoChat.OS.Testing.Neurons;

namespace IntoChat.OS.Testing.Tests.Integration;

// These fixtures start inside the facts, beside the assembly's shared TestingHost, so each fact owns its hosts.
public sealed class BrainHostFixtureFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TwoPeopleInOneHostDoNotSeeEachOthersSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new PulseHost();
        await host.InitializeAsync();
        var alice = await host.PersonAsync("alice", ct);
        var bob = await host.PersonAsync("alice", ct);
        Assert.NotEqual(alice.BrainId, bob.BrainId);
        Assert.NotEqual(alice.AccountId, bob.AccountId);

        await using var alices = await host.ObserveAsync<Pulsed>(host.Brain.Get<IPulse>(alice.Key("pulse")), alice, ct);
        await using var bobs = await host.ObserveAsync<Pulsed>(host.Brain.Get<IPulse>(bob.Key("pulse")), bob, ct);
        using (bob.AsCaller()) { await host.Brain.Get<IPulse>(bob.Key("pulse")).Pulse(2); }
        using (alice.AsCaller()) { await host.Brain.Get<IPulse>(alice.Key("pulse")).Pulse(1); }

        Assert.Equal(1, (await alices.NextAsync(ct: ct)).Value);
        Assert.Equal(2, (await bobs.NextAsync(ct: ct)).Value);
        Assert.Equal([1], alices.Snapshot.Select(pulse => pulse.Value));
        Assert.Equal([2], bobs.Snapshot.Select(pulse => pulse.Value));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WithoutHttpHasNoHttpEdgeAndStillServes()
    {
        var ct = TestContext.Current.CancellationToken;
        // Measured on one machine: the first start with HTTP in a process took about 150 ms and one without about 85 ms
        // (Kestrel and endpoint warm-up, paid once per assembly); warm starts are within noise of each other (~80 ms).
        // A timing assertion would flake beside parallel hosts, so the fact pins what slim mode leaves out.
        await using var slim = new SlimPulseHost();
        await slim.InitializeAsync();
        Assert.False(slim.Brain.Execution.Http);
        Assert.Throws<InvalidOperationException>(() => slim.Brain.HttpClient);
        var person = await slim.PersonAsync("carol", ct);
        using (person.AsCaller()) { await slim.Brain.Get<IPulse>(person.Key("pulse")).Pulse(3); }
        await slim.Brain.RestartAsync(ct);
        Assert.Throws<InvalidOperationException>(() => slim.Brain.HttpClient);
        using (person.AsCaller()) { await slim.Brain.Get<IPulse>(person.Key("pulse")).Pulse(4); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TwoSlimHostsRunSideBySide()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = new SlimPulseHost();
        await using var second = new SlimPulseHost();
        await Task.WhenAll(first.InitializeAsync().AsTask(), second.InitializeAsync().AsTask());
        var people = await Task.WhenAll(first.PersonAsync("dana", ct), second.PersonAsync("erin", ct));
        Assert.NotEqual(people[0].BrainId, people[1].BrainId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStartRetriesOncePortsCollide()
    {
        var ct = TestContext.Current.CancellationToken;
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var blocked = ((IPEndPoint)taken.LocalEndpoint).Port;
        var picks = 0;
        var target = new InProcessTarget(() =>
        {
            if (Interlocked.Increment(ref picks) != 1) { return PortReservation.Reserve(2); }
            using var spare = PortReservation.Reserve(1);
            return PortReservation.Of(blocked, spare.Ports[0]);
        });

        await using var brain = await TestBrain.Create().WithModule<PulseModule>().WithoutHttp().On(target).StartAsync(ct);

        Assert.Equal(2, picks);
        await brain.Get<IPulse>("owner/pulse").Pulse(1);
    }

    private sealed class PulseHost : BrainHostFixture
    {
        protected override TestBrain Configure(TestBrain brain) => brain.WithModule<PulseModule>();
    }

    private sealed class SlimPulseHost : BrainHostFixture
    {
        protected override TestBrain Configure(TestBrain brain) => brain.WithModule<PulseModule>().WithoutHttp();
    }
}
