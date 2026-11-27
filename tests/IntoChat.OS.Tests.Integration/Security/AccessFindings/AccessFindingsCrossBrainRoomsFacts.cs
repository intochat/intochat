using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsCrossBrainRoomsFacts(Gate20EdgeApprovalWallFacts.EdgeKernelFixture fixture)
    : IClassFixture<Gate20EdgeApprovalWallFacts.EdgeKernelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaInvited()
    {
        var host = fixture.Host;
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = SiloNeurons.Get<IThreadDirectory>(
            host.Services, new NeuronId(victim, Threads.Type, IThreadDirectory.DefaultKey));
        var attackerRoom = new NeuronId(attacker, IConversation.Type, "conversations/r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Invited(attackerRoom));
        }
    }

    [Fact]
    public async Task ARegisteredStranger_CannotWriteIntoAnotherBrainsRoomIndex()
    {
        var host = fixture.Host;
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var victim = new BrainId(host.Scope);
        var name = "mallory-" + Guid.NewGuid().ToString("N")[..8];
        using var registered = await host.Client.PostAsync(
            "/identity/register", JsonContent.Create(new { principalId = name, password = "correct-horse-battery-staple", displayName = name }), Ct);
        registered.EnsureSuccessStatusCode();
        var member = await registered.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var strangerBrain = new BrainId(member.GetProperty("brainId").GetString()!);
        var stranger = edge.Person(member.GetProperty("principalId").GetString()!, member.GetProperty("accountId").GetString()!, strangerBrain);
        var trap = new NeuronId(strangerBrain, IConversation.Type, "conversations/trap");
        var index = new NeuronId(victim, Threads.Type, IThreadDirectory.DefaultKey);

        using (edge.Enter(stranger))
        {
            var trapConv = SiloNeurons.Get<IConversation>(host.Services, trap);
            await trapConv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Trap");
            await trapConv.Say("mine");

            var theirs = SiloNeurons.Get<IThreadDirectory>(host.Services, index);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => theirs.Invited(trap));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => theirs.Changed(trap, 7));
        }

        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, victim)))
        {
            Assert.Equal(0, await SiloNeurons.Get<IThreadDirectory>(host.Services, index).LastRevision(trap));
        }
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task OnlyAFriendsRoomWritesIntoARoomIndex(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        var room = new NeuronId(alice.Brain, IConversation.Type, "conversations/hangout");
        var bobsIndex = new NeuronId(bob.Brain, Threads.Type, IThreadDirectory.DefaultKey);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await aliceRoom.Say("one");
        await bob.Get<IConversation>(room).Say("two");

        var expectedRev = (await aliceRoom.Read()).Revision;
        Assert.Equal(expectedRev, await bob.Get<IThreadDirectory>(bobsIndex).LastRevision(room));

        var trap = new NeuronId(mallory.Brain, IConversation.Type, "conversations/trap");
        var strangers = mallory.Get<IThreadDirectory>(bobsIndex);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Invited(trap));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Changed(trap, 7));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => strangers.Changed(room, 99));

        var friends = alice.Get<IThreadDirectory>(bobsIndex);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => friends.Changed(room, 99));

        Assert.Equal(0, await bob.Get<IThreadDirectory>(bobsIndex).LastRevision(trap));
        Assert.Equal(expectedRev, await bob.Get<IThreadDirectory>(bobsIndex).LastRevision(room));
    }

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaChanged()
    {
        var host = fixture.Host;
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = SiloNeurons.Get<IThreadDirectory>(
            host.Services, new NeuronId(victim, Threads.Type, IThreadDirectory.DefaultKey));
        var attackerRoom = new NeuronId(attacker, IConversation.Type, "conversations/r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Changed(attackerRoom, 7));
        }
    }
}
