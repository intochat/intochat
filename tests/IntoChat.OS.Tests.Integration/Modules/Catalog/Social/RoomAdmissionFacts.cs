using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

// Who may talk in a room, and who may write into a person's index of rooms. The kernel lets a person of another brain
// call a [Shared] method of a [Shareable] contract; an [Offered] contract also needs an offer. IRooms is not offered, and
// an offer on a room is not an invitation, so both neurons decide from the stamped caller themselves.
public sealed class RoomAdmissionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NeuronId RoomOf(Person host, string name = "hangout") => new(host.Brain, "social.room", name);

    private static NeuronId IndexOf(Person person) => new(person.Brain, "social.rooms", IRooms.DefaultKey);

    private static IFriends Roster(Person person) => person.Get<IFriends>(FriendsDefaults.Id(person.Brain.Value));

    private static ScreenAction Press => new(null, 1, "key-0");

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AStrangerCannotWriteIntoAnotherPersonsRoomIndex(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, mallory) = (os.Person("alice"), os.Person("bob"), os.Person("mallory"));
        await alice.Befriend(bob, Ct);
        await alice.Get<IRoom>(RoomOf(alice)).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await alice.Get<IRoom>(RoomOf(alice)).Say("one", Ct);

        var index = mallory.Get<IRooms>(IndexOf(bob));
        var trap = RoomOf(mallory, "trap");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(trap, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(trap, 7, Ct));
        // A notice that names a room Bob does follow.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(RoomOf(alice), 99, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(RoomOf(alice), Ct));

        var mine = bob.Get<IRooms>(IndexOf(bob));
        Assert.Equal(0, await mine.LastRevision(trap, Ct));
        Assert.Equal(1, await mine.LastRevision(RoomOf(alice), Ct));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AFriendWritesIntoTheIndexOnlyThroughARoom(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);

        // Alice herself, not her room: the room in the argument proves nothing.
        var index = alice.Get<IRooms>(IndexOf(bob));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Invited(RoomOf(alice), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.Changed(RoomOf(alice), 5, Ct));
        Assert.Equal(0, await bob.Get<IRooms>(IndexOf(bob)).LastRevision(RoomOf(alice), Ct));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARoomOfAFriendNotifiesEveryParticipantWhoeverSpoke(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);
        var room = RoomOf(alice);
        await alice.Get<IRoom>(room).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await alice.Get<IRoom>(room).Invite(carol.Account, carol.Brain, Role.Editor, Ct);

        await alice.Get<IRoom>(room).Say("one", Ct);
        // Bob speaks: the room tells Carol, who is a friend of the host and not of Bob.
        await bob.Get<IRoom>(room).Say("two", Ct);

        Assert.Equal(2, await bob.Get<IRooms>(IndexOf(bob)).LastRevision(room, Ct));
        Assert.Equal(2, await carol.Get<IRooms>(IndexOf(carol)).LastRevision(room, Ct));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TheHostAndAnInvitedFriendTalkInTheRoom(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);
        var room = RoomOf(alice);
        await alice.Get<IRoom>(room).Invite(bob.Account, bob.Brain, Role.Editor, Ct);

        await alice.Get<IRoom>(room).Say("welcome", Ct);
        await bob.Get<IRoom>(room).Here(Ct);
        await bob.Get<IRoom>(room).Say("hello", Ct);

        var seen = await bob.Get<IRoom>(room).Read(null, Ct);
        Assert.Equal(["welcome", "hello"], seen.Messages.Select(message => message.Text));
        Assert.Equal([alice.Account, bob.Account], seen.Messages.Select(message => message.Author));
        Assert.Contains(bob.Account, seen.PresentAccounts);
    }

    // An offer opens the kernel's gate. It is not an invitation: the room knows who it invited.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task APersonWithAnOfferAndNoInvitationIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, carol) = (os.Person("alice"), os.Person("carol"));
        await alice.Befriend(carol, Ct);
        var room = RoomOf(alice);
        await alice.Get<IRoom>(room).Say("private", Ct);
        await alice.Offers.Offer(room, carol.Account, Role.Editor, Ct);

        var uninvited = carol.Get<IRoom>(room);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Read(null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Say("let me in", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Here(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => uninvited.Act("app.calculator", "room-calc", Press, Ct));

        var seen = await alice.Get<IRoom>(room).Read(null, Ct);
        Assert.Equal(["private"], seen.Messages.Select(message => message.Text));
        Assert.Empty(seen.PresentAccounts);
    }

    // A seat and an offer that stand for a person the host's roster does not list: what the room holds between a removal
    // and the turn in which it hears of it. The host's friend check answers yes to everyone while the invitation is made
    // and asks the rosters again afterwards.
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
        await alice.Get<IRoom>(room).Say("private", Ct);

        everyone.On = true;
        await alice.Get<IRoom>(room).Invite(mallory.Account, mallory.Brain, Role.Editor, Ct);
        everyone.On = false;
        // The offers registry answers only inside the cluster: asked as the kernel of the host's brain.
        using (os.Runtime.Edge.EnterKernel(alice.Brain))
        {
            var offers = os.Runtime.Get<IOffers>(new NeuronId(alice.Brain, "component.offers", IOffers.DefaultKey));
            Assert.Equal(Role.Editor, await offers.RoleOf(room, mallory.Account, Ct));
        }

        var seated = mallory.Get<IRoom>(room);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Read(null, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Say("let me in", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Here(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => seated.Act("app.calculator", "room-calc", Press, Ct));

        // The room goes on: an index whose owner does not list the host as a friend refuses the notice, and the message stands.
        await alice.Get<IRoom>(room).Say("still private", Ct);
        Assert.Equal(0, await mallory.Get<IRooms>(IndexOf(mallory)).LastRevision(room, Ct));
        var seen = await alice.Get<IRoom>(room).Read(null, Ct);
        Assert.Equal(["private", "still private"], seen.Messages.Select(message => message.Text));
        Assert.Empty(seen.PresentAccounts);

        // Friendship was the only thing missing: the same seat and the same offer now let her in.
        await alice.Befriend(mallory, Ct);
        await seated.Say("hello", Ct);
        Assert.Equal(3, await mallory.Get<IRooms>(IndexOf(mallory)).LastRevision(room, Ct));
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
        await alice.Get<IRoom>(room).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await bob.Get<IRoom>(room).Say("hello", Ct);
        var index = bob.Get<IRooms>(IndexOf(bob));
        Assert.Equal(1, await index.LastRevision(room, Ct));

        await Roster(alice).Unfriend(new AccountId(bob.Account));

        // Nothing is waited for between the removal and the visit.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IRoom>(room).Say("still here", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IRoom>(room).Read(null, Ct));

        // Friends again is not invited again. The room hears of the removal on a later turn and drops the seat then: from
        // that turn on it no longer tells Bob's index, and Bob stays refused until the host invites him.
        await alice.Befriend(bob, Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await os.Idle();
            var before = await index.LastRevision(room, Ct);
            await alice.Get<IRoom>(room).Say("anyone?", Ct);
            if (await index.LastRevision(room, Ct) == before) { break; }
            Assert.True(DateTime.UtcNow < deadline, "Not within ten seconds: the room dropped the seat of the removed friend.");
            await Task.Delay(50, Ct);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IRoom>(room).Say("back", Ct));

        await alice.Get<IRoom>(room).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await bob.Get<IRoom>(room).Say("back", Ct);
        Assert.Equal((await alice.Get<IRoom>(room).Read(null, Ct)).Revision, await index.LastRevision(room, Ct));
    }
}
