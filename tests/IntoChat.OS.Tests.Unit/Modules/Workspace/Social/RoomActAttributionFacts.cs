using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace.Social;

public sealed class RoomActAttributionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Signal("probe.tap")]
    private sealed record ProbeTap(string? Note = "hi") : Signal;

    private sealed record ProbeState(int Count, string? LastAccount)
    {
        public ProbeState() : this(0, null) { }
    }

    private sealed class CallerRecorder
    {
        public List<Caller?> DispatchedCallers { get; } = [];
    }

    [Shareable]
    [Neuron("test.room-probe")]
    private sealed class RoomProbeScreen(CallerRecorder recorder) : Screen<ProbeState>
    {
        protected override View Render(ProbeState state)
            => new("Probe", new Button("tap", "Tap", Sends(new ProbeTap("pressed"))));

        [On]
        public Task Handle(ProbeTap _)
        {
            recorder.DispatchedCallers.Add(Context.Caller);
            return Show(new ProbeState(State.Count + 1, Context.Caller?.For?.Account));
        }
    }

    [Neuron("test.non-screen")]
    private sealed class NonScreenNeuron : Neuron
    {
    }

    private interface IFakeRoster
    {
        Task EmitRemoved(string account);
    }

    [Neuron(FriendsDefaults.TypeName)]
    private sealed class FakeFriendsRoster : Neuron, IFakeRoster
    {
        public Task EmitRemoved(string account)
            => Publish(new FriendRemoved(new AccountId(account)));
    }

    private sealed class MutableFriendCheck : IFriendCheck
    {
        public HashSet<(string Owner, string Account)> Friends { get; } = [];

        public Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default)
            => Task.FromResult(Friends.Contains((owner.Value, account)));
    }

    private sealed class StaticBrainOwners : IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<string?>(brain.Value);
    }

    private sealed class NoOpSecretRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private static (TestOS Os, CallerRecorder Recorder, MutableFriendCheck Friends) CreateEnvironment()
    {
        var recorder = new CallerRecorder();
        var friends = new MutableFriendCheck();
        EdgeAuthority? capturedEdge = null;
        var sp = new ServiceCollection()
            .AddSingleton(recorder)
            .AddSingleton<IFriendCheck>(friends)
            .AddSingleton<IBrainOwners>(new StaticBrainOwners())
            .AddSingleton<ISecretRedactor>(new NoOpSecretRedactor())
            .AddSingleton(TimeProvider.System)
            .AddSingleton(_ => capturedEdge ?? throw new InvalidOperationException("EdgeAuthority not yet captured."))
            .BuildServiceProvider();

        var os = TestOS.Local(options => options.Services = sp)
            .Register<ConversationNeuron>()
            .Register<ThreadDirectoryNeuron>()
            .Register<RoomProbeScreen>()
            .Register<NonScreenNeuron>()
            .Register<FakeFriendsRoster>();
        capturedEdge = os.Edge;
        return (os, recorder, friends);
    }

    [Fact]
    public async Task Act_PreservesForeignFriendsRealCallerAndRoomProvenance_NeverHostModule()
    {
        var (os, recorder, friends) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        friends.Friends.Add((aliceBrain.Value, "bob"));
        friends.Friends.Add((bobBrain.Value, "alice"));

        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var opened = await os.Get<IScreen>(probeId).Open();
            Assert.NotNull(opened);

            var aliceRoom = os.Get<IConversation>(roomId);
            await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
            await aliceRoom.Pin("test.room-probe", "probe-1");
            await aliceRoom.Invite(new AccountId("bob"), bobBrain, Role.Editor);
        }

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IConversation>(roomId);
            var view = await bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"));
            Assert.NotNull(view);
        }

        var dispatchedCaller = Assert.Single(recorder.DispatchedCallers);
        Assert.NotNull(dispatchedCaller);
        Assert.IsType<Actor.Person>(dispatchedCaller!.Actor);
        Assert.IsNotType<Actor.Module>(dispatchedCaller.Actor);
        Assert.Equal("bob", dispatchedCaller.For?.Account);
        Assert.NotEqual("alice", dispatchedCaller.For?.Account);
        Assert.Equal(bobBrain, dispatchedCaller.Brain);
        Assert.Equal(roomId, dispatchedCaller.Via);
        Assert.True(dispatchedCaller.Inside);
        Assert.False(dispatchedCaller.Direct);
    }

    [Fact]
    public async Task RoleViewer_MayReadAndHere_ButIsRefusedOnActAndSay()
    {
        var (os, recorder, friends) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var carolBrain = new BrainId("carol");
        friends.Friends.Add((aliceBrain.Value, "carol"));
        friends.Friends.Add((carolBrain.Value, "alice"));

        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/lounge");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            var aliceRoom = os.Get<IConversation>(roomId);
            await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Lounge");
            await aliceRoom.Pin("test.room-probe", "probe-1");
            await aliceRoom.Invite(new AccountId("carol"), carolBrain, Role.Viewer);
            await aliceRoom.Say("welcome");
        }

        using (os.Edge.Enter(os.Edge.Person("carol", "carol", carolBrain)))
        {
            var carolRoom = os.Get<IConversation>(roomId);
            await carolRoom.Here();
            var view = await carolRoom.Read();
            Assert.Single(view.Entries.OfType<Said>());
            Assert.Contains("carol", view.PresentAccounts);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => carolRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => carolRoom.Say("viewer cannot write"));
        }

        Assert.Empty(recorder.DispatchedCallers);

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var aliceRoom = os.Get<IConversation>(roomId);
            await aliceRoom.Say("host still writes");
            var afterAct = await aliceRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"));
            Assert.NotNull(afterAct);
        }

        var hostCaller = Assert.Single(recorder.DispatchedCallers);
        Assert.Equal("alice", hostCaller?.For?.Account);
    }

    [Fact]
    public async Task Act_OnUnpinnedScreen_IsRefusedWithActivationRefusedException()
    {
        var (os, recorder, friends) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        friends.Friends.Add((aliceBrain.Value, "bob"));
        friends.Friends.Add((bobBrain.Value, "alice"));

        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "unpinned");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            var aliceRoom = os.Get<IConversation>(roomId);
            await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
            await aliceRoom.Invite(new AccountId("bob"), bobBrain, Role.Editor);
        }

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
                () => os.Get<IConversation>(roomId).Act("test.room-probe", "unpinned", new Command(".", "probe.tap")));
            Assert.Equal(ActivationRefusedException.UnknownChild, refused.Reason);
        }

        Assert.Empty(recorder.DispatchedCallers);
    }

    [Theory]
    [InlineData("os.settings")]
    [InlineData("app.settings")]
    [InlineData("files")]
    [InlineData("app.files")]
    [InlineData("ino")]
    [InlineData("app.ino")]
    [InlineData("identity.brain-keys")]
    [InlineData("identity.vault")]
    [InlineData("social.rooms")]
    [InlineData("tasks")]
    [InlineData("app.tasks")]
    [InlineData("engineer")]
    [InlineData("app.engineer")]
    [InlineData("creator")]
    [InlineData("app.creator")]
    [InlineData("test.non-screen")]
    [InlineData("unregistered.screen")]
    public async Task Pin_RejectsHostPrivateAndNonScreenTypes(string forbiddenScreenType)
    {
        var (os, _, _) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/hangout");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var room = os.Get<IConversation>(roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => room.Pin(forbiddenScreenType, "default"));

            var view = await room.Read();
            Assert.Empty(view.PinnedScreens);
        }
    }

    [Fact]
    public async Task Pin_AllowsRegisteredShareableScreen()
    {
        var (os, _, _) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/hangout");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var room = os.Get<IConversation>(roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
            await room.Pin("test.room-probe", "probe-1");
            var view = await room.Read();
            var pinned = Assert.Single(view.PinnedScreens);
            Assert.Equal("test.room-probe", pinned.ScreenType);
            Assert.Equal("probe-1", pinned.ScreenKey);
        }
    }

    [Fact]
    public async Task Unfriend_ImmediatelyRefusesAllRoomOperations_BeforeAndAfterFriendRemovedSignal()
    {
        var (os, recorder, friends) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        friends.Friends.Add((aliceBrain.Value, "bob"));
        friends.Friends.Add((bobBrain.Value, "alice"));

        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            var aliceRoom = os.Get<IConversation>(roomId);
            await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Hangout");
            await aliceRoom.Pin("test.room-probe", "probe-1");
            await aliceRoom.Invite(new AccountId("bob"), bobBrain, Role.Editor);
        }

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            await os.Get<IConversation>(roomId).Say("before unfriend");
        }

        friends.Friends.Remove((aliceBrain.Value, "bob"));

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IConversation>(roomId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Read());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Say("blocked"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(bobRoom.Here);
        }

        using (os.Edge.EnterKernel(aliceBrain))
        {
            await os.Get<IFakeRoster>(FriendsDefaults.Id(aliceBrain.Value)).EmitRemoved("bob");
        }
        await os.Idle();

        friends.Friends.Add((aliceBrain.Value, "bob"));

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IConversation>(roomId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Read());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Say("still blocked"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(bobRoom.Here);
        }

        Assert.Empty(recorder.DispatchedCallers);
    }

    [Fact]
    public async Task ThreadTrustPosture_IsStrictlyImmutableAfterCreation()
    {
        var (os, _, _) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var ownerThread = new NeuronId(aliceBrain, IConversation.Type, "conversations/owner-only");
        var sharedThread = new NeuronId(aliceBrain, IConversation.Type, "conversations/shared-only");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var ownerConv = os.Get<IConversation>(ownerThread);
            await ownerConv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, "My Squad");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ownerConv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Downgrade"));

            var sharedConv = os.Get<IConversation>(sharedThread);
            await sharedConv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Friends Room");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sharedConv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant, "Upgrade"));
        }
    }

    [Fact]
    public async Task RichMessengerPrimitives_IdempotencyReplyReactionsEditDeleteAndSinceRevisionWork()
    {
        var (os, _, friends) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        friends.Friends.Add((aliceBrain.Value, "bob"));
        friends.Friends.Add((bobBrain.Value, "alice"));

        var roomId = new NeuronId(aliceBrain, IConversation.Type, "conversations/rich-chat");

        EntryId firstId;
        long revAfterFirst;
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var room = os.Get<IConversation>(roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Rich Chat");
            await room.Invite(new AccountId("bob"), bobBrain, Role.Editor);

            // Idempotent MessageId: duplicate call is a no-op.
            await room.Say("Hello world", messageId: "msg-1");
            await room.Say("Hello world", messageId: "msg-1");

            var view = await room.Read();
            var first = Assert.Single(view.Entries.OfType<Said>());
            Assert.Equal("msg-1", first.MessageId);
            firstId = first.Id;
            revAfterFirst = view.Revision;
        }

        // Bob replies to firstId and reacts with emoji.
        EntryId bobReplyId;
        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IConversation>(roomId);
            await bobRoom.Say("Replying!", messageId: "msg-2", replyTo: firstId);
            await bobRoom.React(firstId, "👍");

            var delta = await bobRoom.Read(sinceRevision: revAfterFirst);
            Assert.Equal(2, delta.Entries.OfType<Said>().Count());
            var bobMsg = Assert.Single(delta.Entries.OfType<Said>(), s => s.MessageId == "msg-2");
            Assert.Equal(firstId, bobMsg.ReplyTo);
            bobReplyId = bobMsg.Id;

            var reacted = Assert.Single(delta.Entries.OfType<Said>(), s => s.Id == firstId);
            var reaction = Assert.Single(reacted.Reactions!);
            Assert.Equal("👍", reaction.Emoji);
            Assert.Equal(["bob"], reaction.Accounts);

            // Bob cannot edit Alice's message.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Edit(firstId, "spoofed"));
        }

        // Alice edits her message and tombstones Bob's message (owner privilege).
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var room = os.Get<IConversation>(roomId);
            var revBeforeEdit = (await room.Read()).Revision;
            await room.Edit(firstId, "Hello edited world");
            await room.Delete(bobReplyId);

            var delta = await room.Read(sinceRevision: revBeforeEdit);
            var edited = Assert.Single(delta.Entries.OfType<Said>(), s => s.Id == firstId);
            Assert.Equal("Hello edited world", edited.Text);
            Assert.NotNull(edited.EditedAt);

            var tombstoned = Assert.Single(delta.Entries.OfType<Said>(), s => s.Id == bobReplyId);
            Assert.True(tombstoned.Deleted);
            Assert.Equal(string.Empty, tombstoned.Text);
        }
    }
}
