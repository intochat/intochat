using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Finding 7 (54-friends-merge-design.md): IRooms.Invited / IRooms.Changed are [Shared] on a [Shareable] contract that
// is NOT [Offered], and RoomsNeuron checks nothing itself. The design note warns that a foreign brain which is not a
// member can therefore write entries into another brain's room index. This fact drives the real silo pipeline with a
// caller stamped as a person of a DIFFERENT brain (the cross-brain tier), through the public IRooms proxy, so the
// AccessGate cross-brain wall is the thing under test -- the call is not made directly in-process.
//
// Named for the property that SHOULD hold: a brain that the victim has not befriended cannot mutate the victim's room
// index. It FAILS today if the write is accepted.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsCrossBrainRoomsFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaInvited()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IRooms>(
            host.Services, new NeuronId(victim, "social.rooms", IRooms.DefaultKey));
        var attackerRoom = new NeuronId(attacker, "social.room", "r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            // SHOULD hold: a non-member foreign brain cannot push an entry into the victim's room index.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Invited(attackerRoom, Ct));
        }
    }

    // The same question asked the way a real stranger would: a person who registered on this host, and so has an account,
    // a brain and a membership of their own. The two facts around this one use a caller no directory knows; the grant stage
    // refuses that caller everything, its own brain included, so they say nothing about the index. This caller the stages
    // admit, and before RoomsNeuron checked its caller the write went through.
    [Fact]
    public async Task ARegisteredStranger_CannotWriteIntoAnotherBrainsRoomIndex()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var victim = new BrainId(host.Scope);
        var name = "mallory-" + Guid.NewGuid().ToString("N")[..8];
        using var registered = await host.Client.PostAsync(
            "/identity/register", JsonContent.Create(new { principalId = name, password = "correct-horse-battery-staple", displayName = name }), Ct);
        registered.EnsureSuccessStatusCode();
        var member = await registered.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var strangerBrain = new BrainId(member.GetProperty("brainId").GetString()!);
        var stranger = edge.Person(member.GetProperty("principalId").GetString()!, member.GetProperty("accountId").GetString()!, strangerBrain);
        var trap = new NeuronId(strangerBrain, "social.room", "trap");
        var index = new NeuronId(victim, "social.rooms", IRooms.DefaultKey);

        using (edge.Enter(stranger))
        {
            // The stranger is a real caller: their own room in their own brain serves them.
            await SiloNeurons.Get<IRoom>(host.Services, trap).Say("mine", Ct);

            var theirs = SiloNeurons.Get<IRooms>(host.Services, index);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => theirs.Invited(trap, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => theirs.Changed(trap, 7, Ct));
        }

        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, victim)))
        {
            Assert.Equal(0, await SiloNeurons.Get<IRooms>(host.Services, index).LastRevision(trap, Ct));
        }
    }

    // On both runtimes, with real friendships: a stranger and a friend calling the index themselves are refused, and a
    // friend's room still tells every participant.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task OnlyAFriendsRoomWritesIntoARoomIndex(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        var room = new NeuronId(alice.Brain, "social.room", "hangout");
        var bobsIndex = new NeuronId(bob.Brain, "social.rooms", IRooms.DefaultKey);
        await alice.Get<IRoom>(room).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await alice.Get<IRoom>(room).Say("one", Ct);
        await bob.Get<IRoom>(room).Say("two", Ct);

        // The room told Bob's index, whoever spoke.
        Assert.Equal(2, await bob.Get<IRooms>(bobsIndex).LastRevision(room, Ct));

        // A stranger, with a room of their own and with a room Bob does follow.
        var trap = new NeuronId(mallory.Brain, "social.room", "trap");
        var strangers = mallory.Get<IRooms>(bobsIndex);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Invited(trap, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Changed(trap, 7, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Changed(room, 99, Ct));

        // A friend herself, not her room: the room named in the argument proves nothing.
        var friends = alice.Get<IRooms>(bobsIndex);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => friends.Changed(room, 99, Ct));

        Assert.Equal(0, await bob.Get<IRooms>(bobsIndex).LastRevision(trap, Ct));
        Assert.Equal(2, await bob.Get<IRooms>(bobsIndex).LastRevision(room, Ct));
    }

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaChanged()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IRooms>(
            host.Services, new NeuronId(victim, "social.rooms", IRooms.DefaultKey));
        var attackerRoom = new NeuronId(attacker, "social.room", "r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            // SHOULD hold: likewise for the revision-bump path.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Changed(attackerRoom, 7, Ct));
        }
    }
}
