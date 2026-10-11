using DigitalBrain.Apps;
using DigitalBrain.Framework.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

// T26-7/R134/R146/R147: a shared app's slots point either at the circle's own copy of a chosen connection, or at the
// sharer's connection, which an app of the circle reaches only through the binding recorded on it at Share.
public sealed class SharedConnectionFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AChosenConnectionIsRecordedOnTheCircleAppSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        await Own(brain, alice, "notes", "c1");
        var circle = await Create(brain, alice);

        await alice.In(circle, () => Circle(brain, circle).Share("notes", ["c1"]));

        var copy = ConnectionKeys.For(circle, Connector, "c1");
        var view = await alice.In(circle, () => Grains(brain).GetConnection(copy).Read());
        Assert.Equal((Connector, "c1", ConnectionStatus.Disconnected), (view.ConnectorId, view.ConnectionId, view.Status));
        Assert.Contains(copy, await Grains(brain).GetGrain<IConnectionIndex>(circle).List());
        var app = await alice.In(circle, () => brain.Get<IApp>(IApp.Key(circle, "notes")).Read());
        Assert.Equal(copy, app.Connections["db"]);
    }

    // R134: the binding admits the shared app only, for the connection it was shared with.
    [Fact(Timeout = 120_000)]
    public async Task AnUnrecordedCrossBrainConnectionCallIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        await Own(brain, alice, "notes", "c1");
        await Own(brain, alice, "diary", "c2");
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        await alice.In(circle, () => Circle(brain, circle).Share("notes", []));
        var (left, unshared) = (ConnectionKeys.For(alice.BrainId, Connector, "c1"), ConnectionKeys.For(alice.BrainId, Connector, "c2"));
        Assert.Equal(left, (await alice.In(circle, () => brain.Get<IApp>(IApp.Key(circle, "notes")).Read())).Connections["db"]);

        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        {
            Assert.Equal($"{alice.BrainId}:secret-c1", await Grains(brain).GetConnectionGrain<IShared>(left).Use());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnectionGrain<IShared>(unshared).Use());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnection(left).Disconnect());
        }
        using (bob.AsAppIn(circle, IApp.Key(circle, "circleroom")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnectionGrain<IShared>(left).Use()); }
        using (bob.AsCallerIn(circle))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnectionGrain<IShared>(left).Use()); }
    }

    // R146 (finding 1): a binding admits only a member of the circle: a stranger stamped as the shared app is refused.
    [Fact(Timeout = 120_000)]
    public async Task AStrangerStampedAsTheSharedAppIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, carol) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("carol", ct));
        await Own(brain, alice, "notes", "c1");
        var circle = await Create(brain, alice);
        await alice.In(circle, () => Circle(brain, circle).Share("notes", []));
        var left = ConnectionKeys.For(alice.BrainId, Connector, "c1");

        using (carol.AsAppIn(circle, IApp.Key(circle, "notes")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnectionGrain<IShared>(left).Use()); }
        using (alice.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{alice.BrainId}:secret-c1", await Grains(brain).GetConnectionGrain<IShared>(left).Use()); }
    }

    // R155: removing the sharer withdraws its binding: the circle's app no longer reaches the connection it left behind.
    [Fact(Timeout = 120_000)]
    public async Task ARemovedSharersConnectionIsNoLongerReachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        await Own(brain, bob, "notes", "c1");
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        await bob.In(circle, () => Circle(brain, circle).Share("notes", []));
        var left = ConnectionKeys.For(bob.BrainId, Connector, "c1");
        using (alice.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{bob.BrainId}:secret-c1", await Grains(brain).GetConnectionGrain<IShared>(left).Use()); }

        await alice.In(circle, () => Circle(brain, circle).Remove(bob.AccountId));

        using (alice.AsAppIn(circle, IApp.Key(circle, "notes")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(brain).GetConnectionGrain<IShared>(left).Use()); }
        Assert.Equal("", (await alice.In(circle, () => brain.Get<IApp>(IApp.Key(circle, "notes")).Read())).Connections["db"]);
    }

    // Finding 1: from a removed member's own machine, a call stamped as the circle's app is refused.
    [Fact(Timeout = 240_000)]
    public async Task ARemovedMembersMachineIsRefusedOnABoundCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, bob, circle, left) = await LeftBehind(machines, ct);
        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{alice.BrainId}:secret-c1", await Grains(b).GetConnectionGrain<IShared>(left).Use()); }

        await alice.In(circle, () => Circle(a, circle).Remove(bob.AccountId));

        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(b).GetConnectionGrain<IShared>(left).Use()); }
    }

    // Finding 2: a machine serving only the removed member is withdrawn, however it was contributed.
    [Fact(Timeout = 240_000)]
    public async Task AMachineServingOnlyARemovedMemberIsWithdrawn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, bob, circle, _) = await LeftBehind(machines, ct);
        var onB = b.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>();
        await onB.ContributeMachine(circle, onB.MachineId!);
        Assert.Contains(onB.MachineId!, await onB.MachinesOf(circle));

        await alice.In(circle, () => Circle(a, circle).Remove(bob.AccountId));

        Assert.DoesNotContain(onB.MachineId!, await onB.MachinesOf(circle));
        Assert.Contains(a.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().MachineId!, await onB.MachinesOf(circle));
    }

    // Finding 2: a member contributes only the machine its own call comes from, never another member's.
    [Fact(Timeout = 240_000)]
    public async Task AMemberContributesOnlyTheMachineItCallsFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, _, circle, _) = await LeftBehind(machines, ct);
        var bobs = b.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().MachineId!;
        var before = await a.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().MachinesOf(circle);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => alice.In(circle, () => Circle(a, circle).Contribute(bobs)));

        Assert.Equal(before, await a.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().MachinesOf(circle));
    }

    // R146/R149: from bob's machine, the circle app's call to alice's connection is forwarded to alice's core holder,
    // which admits it through the binding.
    [Fact(Timeout = 240_000)]
    public async Task AConnectionLeftBehindRoutesToTheOwnersCore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, bob, circle, left) = await LeftBehind(machines, ct);

        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{alice.BrainId}:secret-c1", await Grains(b).GetConnectionGrain<IShared>(left).Use()); }
        using (bob.AsAppIn(circle, IApp.Key(circle, "circleroom")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Grains(b).GetConnectionGrain<IShared>(left).Use()); }
        Assert.NotNull(a);
    }

    [Fact(Timeout = 240_000)]
    public async Task AConnectionLeftBehindIs503WhileTheOwnerIsOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, _, bob, circle, left) = await LeftBehind(machines, ct);

        await a.DisposeAsync();

        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { await Assert.ThrowsAsync<ServiceUnavailableException>(() => Grains(b).GetConnectionGrain<IShared>(left).Use()); }
    }

    // Alice's machine A holds her brain and the circle; bob's machine B holds his. Alice shares "notes", whose slot
    // stays bound to her connection c1.
    private static async Task<(ITestBrain A, ITestBrain B, Person Alice, Person Bob, string Circle, string Left)> LeftBehind(CircleMachines machines, CancellationToken ct)
    {
        var a = await machines.Machine(ct);
        var b = await machines.Machine(ct);
        var (alice, bob) = (await a.PersonAsync("alice", ct), await b.PersonAsync("bob", ct));
        await Own(a, alice, "notes", "c1");
        var circle = await Create(a, alice);
        await Join(a, circle, alice, bob);
        await alice.In(circle, () => Circle(a, circle).Share("notes", []));
        return (a, b, alice, bob, circle, ConnectionKeys.For(alice.BrainId, Connector, "c1"));
    }
}
