using System.Collections.Concurrent;
using IntoChat.OS.Apps;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Orleans.Storage;
using static IntoChat.OS.Circles.Tests.CircleBrainRooms;

namespace IntoChat.OS.Circles.Tests;

// T26-7/R134/R146/R147: a shared app's slots point either at the circle's own copy of a chosen connection, or at the
// sharer's connection, which an app of the circle reaches only through the binding recorded on it at Share.
public sealed class SharedConnectionFacts(CirclesHost host)
{
    [Fact(Timeout = 120_000)]
    public async Task AChosenConnectionIsRecordedOnTheCircleAppSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        await Own(brain, alice, "notes", "c1");
        var circle = await Create(brain, alice);

        await alice.In(circle, () => Circle(brain, circle).Share("notes", ["c1"]));

        var copy = ConnectionKeys.For(circle, Connector, "c1");
        var view = await alice.In(circle, () => Grains(brain).GetConnection(copy).Read());
        Assert.Equal((Connector, "c1", ConnectionStatus.Connected), (view.ConnectorId, view.ConnectionId, view.Status));
        Assert.Contains(copy, await Grains(brain).GetGrain<IConnectionIndex>(circle).List());
        var app = await alice.In(circle, () => brain.Get<IApp>(IApp.Key(circle, "notes")).Read());
        Assert.Equal(copy, app.Connections["db"]);
    }

    // R134: the binding admits the shared app only, for the connection it was shared with.
    [Fact(Timeout = 120_000)]
    public async Task AnUnrecordedCrossBrainConnectionCallIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var (alice, bob) = (await host.PersonAsync("alice", ct), await host.PersonAsync("bob", ct));
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
        var brain = host.Brain;
        var (alice, carol) = (await host.PersonAsync("alice", ct), await host.PersonAsync("carol", ct));
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
        var brain = host.Brain;
        var (alice, bob) = (await host.PersonAsync("alice", ct), await host.PersonAsync("bob", ct));
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
    [Trait("host", "own")]
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


    // Finding 2: a member contributes only the machine its own call comes from, never another member's.


    // R146/R149: from bob's machine, the circle app's call to alice's connection is forwarded to alice's core holder,
    // which admits it through the binding.
    [Trait("host", "own")]
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



    // R130: the owner's machine re-seals the chosen credential under the circle's core part key, so a member machine
    // opens it and no plaintext crosses a call.
    [Trait("host", "own")]
    [Fact(Timeout = 240_000)]
    public async Task AChosenConnectionIsSealedUnderTheCircleKeyAndOpensOnAMemberMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, bob, circle, copy) = await Chosen(machines, ct);

        var view = await alice.In(circle, () => Grains(a).GetConnection(copy).Read());
        Assert.Equal(ConnectionStatus.Connected, view.Status);
        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{circle}:secret-c1", await Grains(b).GetConnectionGrain<IShared>(copy).Use()); }
    }

    // R130: a refresh of a shared copy's credential keeps it under the circle's key, so every contributed machine
    // opens it (the current write path would re-seal under the refreshing machine's master key).
    [Trait("host", "own")]
    [Fact(Timeout = 240_000)]
    public async Task ARefreshedSharedCredentialStaysOpenableOnAnotherMemberMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var (a, b, alice, _, circle, copy) = await Chosen(machines, ct);
        var before = await CopySecrets(a, copy);
        Assert.True(BrainSeal.IsBrainSealed(Assert.Single(before, secret => secret.Name == "value")), $"imported as {string.Join(",", before.Select(s => s.KeyId))}");
        using (alice.AsCallerIn(circle))
        {
            await Grains(a).GetConnectionInbound(copy).AcceptGrant(new("oauth", "access-0", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(-1), "read", null, "{}"));
        }
        var granted = await CopySecrets(a, copy);
        Assert.True(BrainSeal.IsBrainSealed(Assert.Single(granted, secret => secret.Name == "access")), $"granted as {string.Join(",", granted.Select(s => s.KeyId))}");
        using (alice.AsAppIn(circle, IApp.Key(circle, "notes")))
        {
            Assert.Equal($"{circle}:refreshed-access", await Grains(a).GetConnectionGrain<IShared>(copy).Use());
        }

        var secrets = await CopySecrets(a, copy);
        var access = Assert.Single(secrets, secret => secret.Name == "access");
        Assert.True(BrainSeal.IsBrainSealed(access), $"sealed as {access.KeyId}");
        Assert.Equal("refreshed-access", await b.Services.GetRequiredService<IBrainSecretSealer>().Open(circle, access, ct));
    }

    [Trait("host", "own")]
    [Fact(Timeout = 240_000)]
    public async Task ReSealingSendsNoPlaintextOverAnyCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var a = await machines.Machine(ct, Recorded);
        var b = await machines.Machine(ct, Recorded);
        var (alice, bob) = (await a.PersonAsync("alice", ct), await b.PersonAsync("bob", ct));
        await Own(a, alice, "notes", "c1");
        var circle = await Create(a, alice);
        await Join(a, circle, alice, bob);
        RecordedCalls.Requests.Clear();

        await alice.In(circle, () => Circle(a, circle).Share("notes", ["c1"]));

        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret-c1");
        Assert.NotEmpty(RecordedCalls.Requests);
        Assert.DoesNotContain(RecordedCalls.Requests, request => Contains(request, plaintext));
    }



    // R124/R130: any member may share, not only the creator. The circle holder (alice's machine A) re-seals a
    // non-creator member's connection on the member's own machine B, which must admit A's forwarded read.
    [Trait("host", "own")]
    [Fact(Timeout = 240_000)]
    public async Task ANonCreatorMemberSharesAConnectionSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await CircleMachines.Start(ct);
        var a = await machines.Machine(ct);
        var b = await machines.Machine(ct);
        var (alice, bob) = (await a.PersonAsync("alice", ct), await b.PersonAsync("bob", ct));
        await Own(b, bob, "notes", "c1");
        var circle = await Create(a, alice);
        await Join(a, circle, alice, bob);
        await b.Services.GetRequiredService<IIdentityClient>().EnsureMember(new Member
        { PrincipalId = bob.AccountId, AccountId = bob.AccountId, BrainId = circle, Role = MemberRole.Member, DisplayName = bob.AccountId });

        await bob.In(circle, () => Circle(b, circle).Share("notes", ["c1"]));

        var copy = ConnectionKeys.For(circle, Connector, "c1");
        using (bob.AsAppIn(circle, IApp.Key(circle, "notes")))
        { Assert.Equal($"{circle}:secret-c1", await Grains(b).GetConnectionGrain<IShared>(copy).Use()); }
    }

    private static async Task<SealedSecret[]> CopySecrets(ITestBrain machine, string copy)
    {
        var storage = machine.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage);
        var state = new GrainState<SharedState>();
        await storage.ReadStateAsync("shared", Grains(machine).GetConnection(copy).GetGrainId(), state);
        return state.State!.Secrets;
    }

    private static TestBrain Recorded(TestBrain brain)
        => brain.ConfigureServices(services => services.AddSingleton<IIncomingGrainCallFilter>(provider =>
            new RecordedCalls(provider.GetRequiredService<Serializer>())));

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    // Alice's machine A holds her brain and the circle; bob's machine B holds his. Alice shares "notes" with the
    // chosen connection c1, whose circle copy is the one sealed under the circle's core part key.
    private static async Task<(ITestBrain A, ITestBrain B, Person Alice, Person Bob, string Circle, string Copy)> Chosen(CircleMachines machines, CancellationToken ct)
    {
        var a = await machines.Machine(ct);
        var b = await machines.Machine(ct);
        var (alice, bob) = (await a.PersonAsync("alice", ct), await b.PersonAsync("bob", ct));
        await Own(a, alice, "notes", "c1");
        var circle = await Create(a, alice);
        await Join(a, circle, alice, bob);
        // A forwarded call's member standing matches a machine's served accounts against memberships (R128); a Person's
        // account differs from its principal, so bob's account is recorded as a member too, then B contributes.
        await b.Services.GetRequiredService<IIdentityClient>().EnsureMember(new Member
        { PrincipalId = bob.AccountId, AccountId = bob.AccountId, BrainId = circle, Role = MemberRole.Member, DisplayName = bob.AccountId });
        await alice.In(circle, () => Circle(a, circle).Share("notes", ["c1"]));
        return (a, b, alice, bob, circle, ConnectionKeys.For(circle, Connector, "c1"));
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

// Every grain call a machine sees, as serialized bytes: what a re-seal must not carry as plaintext.
internal sealed class RecordedCalls(Serializer serializer) : IIncomingGrainCallFilter
{
    public static readonly ConcurrentBag<byte[]> Requests = [];

    public Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is IGrainBase) { Requests.Add(serializer.SerializeToArray(context.Request)); }
        return context.Invoke();
    }
}
