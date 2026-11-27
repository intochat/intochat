using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

public sealed class RoomAdmissionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NeuronId RoomOf(Person host, string name = "hangout") => new(host.Brain, IConversation.Type, "conversations/" + name);

    private static NeuronId IndexOf(Person person) => new(person.Brain, Threads.Type, IThreadDirectory.DefaultKey);

    private static IFriends Roster(Person person) => person.Get<IFriends>(FriendsDefaults.Id(person.Brain.Value));

    private static DigitalBrain.UI.Command Press => new(".", "calculator.press");

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AStrangerCannotWriteIntoAnotherPersonsRoomIndex(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        var aliceRoom = alice.Get<IConversation>(RoomOf(alice));
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await aliceRoom.Say("one");

        var index = mallory.Get<IThreadDirectory>(IndexOf(bob));
        var trap = RoomOf(mallory, "trap");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(trap));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(trap, 7));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(RoomOf(alice), 99));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(RoomOf(alice)));

        var mine = bob.Get<IThreadDirectory>(IndexOf(bob));
        Assert.Equal(0, await mine.LastRevision(trap));
        Assert.True(await mine.LastRevision(RoomOf(alice)) >= 1);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AFriendWritesIntoTheIndexOnlyThroughARoom(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);

        var index = alice.Get<IThreadDirectory>(IndexOf(bob));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(RoomOf(alice)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(RoomOf(alice), 5));
        Assert.Equal(0, await bob.Get<IThreadDirectory>(IndexOf(bob)).LastRevision(RoomOf(alice)));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARoomOfAFriendNotifiesEveryParticipantWhoeverSpoke(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);
        var room = RoomOf(alice);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await aliceRoom.Invite(new AccountId(carol.Account), carol.Brain, Role.Editor);

        await aliceRoom.Say("one");
        await bob.Get<IConversation>(room).Say("two");

        var expectedRev = (await aliceRoom.Read()).Revision;
        Assert.Equal(expectedRev, await bob.Get<IThreadDirectory>(IndexOf(bob)).LastRevision(room));
        Assert.Equal(expectedRev, await carol.Get<IThreadDirectory>(IndexOf(carol)).LastRevision(room));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TheHostAndAnInvitedFriendTalkInTheRoom(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        var room = RoomOf(alice);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);

        await aliceRoom.Say("welcome");
        await bob.Get<IConversation>(room).Here();
        await bob.Get<IConversation>(room).Say("hello");

        var seen = await bob.Get<IConversation>(room).Read();
        var said = seen.Entries.OfType<Said>().ToArray();
        Assert.Equal(["welcome", "hello"], said.Select(message => message.Text));
        Assert.Equal([alice.Account, bob.Account], said.Select(message => message.Sender?.AccountId));
        Assert.Contains(bob.Account, seen.PresentAccounts);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APersonWithAnOfferAndNoInvitationIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, carol) = (os.Person("alice"), os.Person("carol"));
        await alice.Befriend(carol, Ct);
        var room = RoomOf(alice);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Say("private");
        await alice.Offers.Offer(room, carol.Account, Role.Editor, Ct);

        var uninvited = carol.Get<IConversation>(room);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Read());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Say("let me in"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(uninvited.Here);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Act("app.calculator", "room-calc", Press));

        var seen = await aliceRoom.Read();
        Assert.Equal(["private"], seen.Entries.OfType<Said>().Select(message => message.Text));
        Assert.Empty(seen.PresentAccounts);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ASeatAndAnOfferAreNotEnoughWithoutFriendship(NeuronRuntimeKind kind)
    {
        var everyone = new Switch();
        await using var os = await Journey.Start(
            kind,
            fakes => fakes.Configure(services => services.AddSingleton<IFriendCheck>(
                host => new YesWhile(everyone, new FriendsFriendCheck(host.GetRequiredService<INeuronProxies>())))),
            Ct);
        var (alice, mallory) = (os.Person("alice"), os.Person("mallory"));
        var room = RoomOf(alice);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Say("private");

        everyone.On = true;
        await aliceRoom.Invite(new AccountId(mallory.Account), mallory.Brain, Role.Editor);
        everyone.On = false;
        using (os.Runtime.Edge.EnterKernel(alice.Brain))
        {
            var offers = os.Runtime.Get<IOffers>(new NeuronId(alice.Brain, "component.offers", IOffers.DefaultKey));
            Assert.Equal(Role.Editor, await offers.RoleOf(room, mallory.Account, Ct));
        }

        var seated = mallory.Get<IConversation>(room);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Read());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Say("let me in"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(seated.Here);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Act("app.calculator", "room-calc", Press));

        await aliceRoom.Say("still private");
        Assert.Equal(0, await mallory.Get<IThreadDirectory>(IndexOf(mallory)).LastRevision(room));
        var seen = await aliceRoom.Read();
        Assert.Equal(["private", "still private"], seen.Entries.OfType<Said>().Select(message => message.Text));
        Assert.Empty(seen.PresentAccounts);

        await alice.Befriend(mallory, Ct);
        await seated.Say("hello");
        var expectedRev = (await aliceRoom.Read()).Revision;
        Assert.Equal(expectedRev, await mallory.Get<IThreadDirectory>(IndexOf(mallory)).LastRevision(room));
    }

    private sealed class Switch
    {
        public volatile bool On;
    }

    private sealed class YesWhile(Switch everyone, IFriendCheck rosters) : IFriendCheck
    {
        public Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default)
            => everyone.On ? Task.FromResult(true) : rosters.IsFriend(owner, account, ct);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARemovedFriendIsRefusedAtOnceAndLosesTheSeat(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        var room = RoomOf(alice);
        var aliceRoom = alice.Get<IConversation>(room);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await bob.Get<IConversation>(room).Say("hello");
        var index = bob.Get<IThreadDirectory>(IndexOf(bob));
        Assert.True(await index.LastRevision(room) >= 1);

        await Roster(alice).Unfriend(new AccountId(bob.Account));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IConversation>(room).Say("still here"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IConversation>(room).Read());

        await alice.Befriend(bob, Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await os.Idle();
            var before = await index.LastRevision(room);
            await aliceRoom.Say("anyone?");
            if (await index.LastRevision(room) == before) { break; }
            Assert.True(DateTime.UtcNow < deadline, "Not within ten seconds: the thread dropped the seat of the removed friend.");
            await Task.Delay(50, Ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IConversation>(room).Say("back"));

        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await bob.Get<IConversation>(room).Say("back");
        Assert.Equal((await aliceRoom.Read()).Revision, await index.LastRevision(room));
    }
}
