using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
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

    private static (TestOS Os, CallerRecorder Recorder, MutableFriendCheck Friends) CreateEnvironment()
    {
        var recorder = new CallerRecorder();
        var friends = new MutableFriendCheck();
        EdgeAuthority? capturedEdge = null;
        var sp = new ServiceCollection()
            .AddSingleton(recorder)
            .AddSingleton<IFriendCheck>(friends)
            .AddSingleton<IBrainOwners>(new StaticBrainOwners())
            .AddSingleton(_ => capturedEdge ?? throw new InvalidOperationException("EdgeAuthority not yet captured."))
            .BuildServiceProvider();

        var os = TestOS.Local(options => options.Services = sp)
            .Register<RoomNeuron>()
            .Register<RoomsNeuron>()
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

        var roomId = new NeuronId(aliceBrain, "social.room", "hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var opened = await os.Get<IScreen>(probeId).Open();
            Assert.NotNull(opened);

            var aliceRoom = os.Get<IRoom>(roomId);
            await aliceRoom.Pin("test.room-probe", "probe-1", Ct);
            await aliceRoom.Invite("bob", bobBrain, Role.Editor, Ct);
        }

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IRoom>(roomId);
            var view = await bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"), Ct);
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

        var roomId = new NeuronId(aliceBrain, "social.room", "lounge");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            var aliceRoom = os.Get<IRoom>(roomId);
            await aliceRoom.Pin("test.room-probe", "probe-1", Ct);
            await aliceRoom.Invite("carol", carolBrain, Role.Viewer, Ct);
            await aliceRoom.Say("welcome", Ct);
        }

        using (os.Edge.Enter(os.Edge.Person("carol", "carol", carolBrain)))
        {
            var carolRoom = os.Get<IRoom>(roomId);
            await carolRoom.Here(Ct);
            var view = await carolRoom.Read(null, Ct);
            Assert.Single(view.Messages);
            Assert.Contains("carol", view.PresentAccounts);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => carolRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"), Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => carolRoom.Say("viewer cannot write", Ct));
        }

        Assert.Empty(recorder.DispatchedCallers);

        // Host remains allowed to Act and Say.
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var aliceRoom = os.Get<IRoom>(roomId);
            await aliceRoom.Say("host still writes", Ct);
            var afterAct = await aliceRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"), Ct);
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

        var roomId = new NeuronId(aliceBrain, "social.room", "hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "unpinned");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            await os.Get<IRoom>(roomId).Invite("bob", bobBrain, Role.Editor, Ct);
        }

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
                () => os.Get<IRoom>(roomId).Act("test.room-probe", "unpinned", new Command(".", "probe.tap"), Ct));
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
        var roomId = new NeuronId(aliceBrain, "social.room", "hangout");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => os.Get<IRoom>(roomId).Pin(forbiddenScreenType, "default", Ct));

            var view = await os.Get<IRoom>(roomId).Read(null, Ct);
            Assert.Empty(view.PinnedScreens);
        }
    }

    [Fact]
    public async Task Pin_AllowsRegisteredShareableScreen()
    {
        var (os, _, _) = CreateEnvironment();
        await using var lifetime = os;

        var aliceBrain = new BrainId("alice");
        var roomId = new NeuronId(aliceBrain, "social.room", "hangout");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            await os.Get<IRoom>(roomId).Pin("test.room-probe", "probe-1", Ct);
            var view = await os.Get<IRoom>(roomId).Read(null, Ct);
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

        var roomId = new NeuronId(aliceBrain, "social.room", "hangout");
        var probeId = new NeuronId(aliceBrain, "test.room-probe", "probe-1");

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            _ = await os.Get<IScreen>(probeId).Open();
            var aliceRoom = os.Get<IRoom>(roomId);
            await aliceRoom.Pin("test.room-probe", "probe-1", Ct);
            await aliceRoom.Invite("bob", bobBrain, Role.Editor, Ct);
        }

        // Bob is admitted while friends.
        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            await os.Get<IRoom>(roomId).Say("before unfriend", Ct);
        }

        // 1. Immediate revocation: IFriendCheck returns false BEFORE FriendRemoved signal arrives.
        friends.Friends.Remove((aliceBrain.Value, "bob"));

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IRoom>(roomId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Read(null, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Say("blocked", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"), Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Here(Ct));
        }

        // 2. Deliver FriendRemoved signal to the room (from Alice's friends roster).
        using (os.Edge.EnterKernel(aliceBrain))
        {
            await os.Get<IFakeRoster>(FriendsDefaults.Id(aliceBrain.Value)).EmitRemoved("bob");
        }
        await os.Idle();

        // Even if friendship is restored later, Bob's seat and offers were revoked until re-invited.
        friends.Friends.Add((aliceBrain.Value, "bob"));

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var bobRoom = os.Get<IRoom>(roomId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Read(null, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Say("still blocked", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Act("test.room-probe", "probe-1", new Command(".", "probe.tap"), Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Here(Ct));
        }

        Assert.Empty(recorder.DispatchedCallers);
    }
}
