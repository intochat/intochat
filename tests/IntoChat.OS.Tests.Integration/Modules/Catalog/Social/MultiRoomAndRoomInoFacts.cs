using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

public sealed class MultiRoomAndRoomInoFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NeuronId IndexOf(Person person) => new(person.Brain, "social.rooms", IRooms.DefaultKey);

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task MultiRoomCreateListInviteLeaveAndReactiveSignalsWork(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);

        // Open Alice's FriendsApp so it subscribes to RoomsIndexChanged and RoomChanged.
        await using var aliceFriends = await alice.Open<FriendsApp>("default", Ct);

        var aliceIndex = alice.Get<IRooms>(IndexOf(alice));
        var dmId = await aliceIndex.Create("Bob", RoomKind.Direct, "dm-bob", Ct);
        var groupId = await aliceIndex.Create("Weekend Trip", RoomKind.Group, "trip-group", Ct);
        await alice.Get<IRoom>(dmId).Configure("Bob", RoomKind.Direct, Ct);
        await alice.Get<IRoom>(dmId).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await aliceIndex.Invite(dmId, bob.Account, bob.Brain, Role.Editor, Ct);
        await alice.Get<IRoom>(groupId).Configure("Weekend Trip", RoomKind.Group, Ct);
        await alice.Get<IRoom>(groupId).Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await alice.Get<IRoom>(groupId).Invite(carol.Account, carol.Brain, Role.Viewer, Ct);
        await aliceIndex.Invite(groupId, bob.Account, bob.Brain, Role.Editor, Ct);

        var aliceRooms = await aliceIndex.List(Ct);
        Assert.Equal(2, aliceRooms.Length);
        Assert.Contains(aliceRooms, r => r.Room == dmId && r.Kind == RoomKind.Direct && r.IsHost);
        Assert.Contains(aliceRooms, r => r.Room == groupId && r.Kind == RoomKind.Group && r.IsHost);

        var bobIndex = bob.Get<IRooms>(IndexOf(bob));
        var bobRooms = await bobIndex.List(Ct);
        Assert.Equal(2, bobRooms.Length);
        Assert.All(bobRooms, r => Assert.False(r.IsHost));

        // Bob leaves the group room; it disappears from Bob's index and Bob loses his seat.
        await bob.Get<IRoom>(groupId).Leave(Ct);
        await bobIndex.Leave(groupId, Ct);
        var bobRoomsAfterLeave = await bobIndex.List(Ct);
        Assert.Single(bobRoomsAfterLeave);
        Assert.DoesNotContain(bobRoomsAfterLeave, r => r.Room == groupId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IRoom>(groupId).Say("after leaving", Ct));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task NormalMessagesTriggerZeroAgentTurnsAndAtInoTriggersRoomScopedAgentWithSpeakerAttribution(NeuronRuntimeKind kind)
    {
        var recording = new RecordingInference("Room-only reply for Alice and Bob.");
        await using var os = await Journey.Start(
            kind,
            fakes => fakes.Configure(services => services.AddSingleton<IModelInference>(recording)),
            Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));
        await alice.Befriend(bob, Ct);

        var aliceIndex = alice.Get<IRooms>(IndexOf(alice));
        var roomId = await aliceIndex.Create("Design Sync", RoomKind.Group, "design-sync", Ct);
        await alice.Get<IRoom>(roomId).Configure("Design Sync", RoomKind.Group, Ct);
        await alice.Get<IRoom>(roomId).Invite(bob.Account, bob.Brain, Role.Editor, Ct);

        // 1. Normal human-to-human messages trigger ZERO agent turns.
        await alice.Get<IRoom>(roomId).Say("Hey Bob, ready to review?", Ct);
        await bob.Get<IRoom>(roomId).Say("Yep, let's go!", Ct);
        Assert.Equal(0, recording.CallCount);

        // 2. Explicit @Ino mention triggers room-scoped agent turn with authenticated speaker attribution.
        await bob.Get<IRoom>(roomId).Say("@Ino summarize our plan", Ct);
        Assert.Equal(1, recording.CallCount);
        var lastRequest = Assert.Single(recording.Requests);
        Assert.Contains(lastRequest.Messages, m => m.Role == "system" && m.Content.OfType<AiText>().Any(t => t.Text.Contains("@Ino · Room only", StringComparison.Ordinal)));
        Assert.Contains(lastRequest.Messages, m => m.Sender?.AccountId == alice.Account);
        Assert.Contains(lastRequest.Messages, m => m.Sender?.AccountId == bob.Account);

        // 3. Alice's view attributes Alice as Self, Bob as Human, and @Ino as Agent.
        var aliceView = await alice.Get<IRoom>(roomId).Read(null, Ct);
        Assert.Equal(4, aliceView.Messages.Length);
        Assert.Equal(SenderKind.Self, aliceView.Messages[0].Sender?.Kind);
        Assert.Equal(alice.Account, aliceView.Messages[0].Sender?.AccountId);
        Assert.Equal(SenderKind.Human, aliceView.Messages[1].Sender?.Kind);
        Assert.Equal(bob.Account, aliceView.Messages[1].Sender?.AccountId);
        Assert.Equal(SenderKind.Agent, aliceView.Messages[3].Sender?.Kind);
        Assert.Equal(RoomAgentDefinition.DisplayName, aliceView.Messages[3].Sender?.DisplayName);

        // 4. Bob's view attributes Bob as Self and Alice as Human.
        var bobView = await bob.Get<IRoom>(roomId).Read(null, Ct);
        Assert.Equal(SenderKind.Human, bobView.Messages[0].Sender?.Kind);
        Assert.Equal(SenderKind.Self, bobView.Messages[1].Sender?.Kind);
        Assert.Equal(SenderKind.Agent, bobView.Messages[3].Sender?.Kind);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task RoomPinEnforcesShareableAndRoomActEnforcesEditorRoleWithCallerAttributionOnCheckBoxNeuron(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);

        var roomId = new NeuronId(alice.Brain, "social.room", "checklist-room");
        var aliceRoom = alice.Get<IRoom>(roomId);
        await aliceRoom.Invite(bob.Account, bob.Brain, Role.Editor, Ct);
        await aliceRoom.Invite(carol.Account, carol.Brain, Role.Viewer, Ct);

        // 1. Non-[Shareable] screens (e.g. personal FriendsApp) are rejected by RoomNeuron.Pin.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => aliceRoom.Pin("app.friends", "default", Ct));

        // 2. [Shareable] RoomChecklistApp (backed by CheckBoxNeuron) is allowed by RoomNeuron.Pin.
        const string checklistKey = "trip-checklist";
        await using var checklistSession = await alice.Open<RoomChecklistApp>(checklistKey, Ct);
        await aliceRoom.Pin("room.checklist", checklistKey, Ct);

        // Host adds initial item (wires CheckBoxNeuron child grain on host brain).
        var hostAddCmd = new Command(".", "room-checklist.add", System.Text.Json.JsonSerializer.SerializeToElement(new { label = "Book flights" }));
        await aliceRoom.Act("room.checklist", checklistKey, hostAddCmd, Ct);

        // 3. Viewer (Carol) is rejected by RoomNeuron.Act (Role.Editor enforced).
        var addCmd = new Command(".", "room-checklist.add", System.Text.Json.JsonSerializer.SerializeToElement(new { label = "Reserve hotel" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.Get<IRoom>(roomId).Act("room.checklist", checklistKey, addCmd, Ct));

        // 4. Editor (Bob) acts on the pinned RoomChecklistApp; Caller.For.Account is propagated as Bob (no AsSocialModule elevation).
        var afterAdd = await bob.Get<IRoom>(roomId).Act("room.checklist", checklistKey, addCmd, Ct);
        Assert.Contains($"last updated by {bob.Account}", afterAdd.View.GetRawText(), StringComparison.Ordinal);

        // 5. Interactive CheckBoxNeuron child updates through RoomNeuron.Act -> Screen.Dispatch -> CheckBoxNeuron -> Toggled.
        var toggleCheckBox = new Command(
            RoomChecklistApp.CheckKey("i1"),
            "control.setChecked",
            System.Text.Json.JsonSerializer.SerializeToElement(new { isOn = true }));
        var afterCheck = await aliceRoom.Act("room.checklist", checklistKey, toggleCheckBox, Ct);
        Assert.NotNull(afterCheck);
        await os.Idle();
        var latestView = await alice.Get<IScreen>(new NeuronId(alice.Brain, "room.checklist", checklistKey)).Read();
        Assert.NotNull(latestView);
        Assert.Contains("1/2 done", latestView!.View.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class RecordingInference(string reply) : IModelInference
    {
        public List<InferenceRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;

        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor("fake", "fake-model", null, "1", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            Requests.Add(request);
            return Task.FromResult(new InferenceResult(
                [new AiMessage("assistant", [new AiText(reply)])],
                "stop",
                new InferenceUsage(10, 10, 20),
                "resp-1",
                "fake-model"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            ModelId? model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            string? brainId)
        {
            Requests.Add(request);
            yield return new InferenceUpdate("assistant", [new AiText(reply)], "stop", new InferenceUsage(10, 10, 20), "resp-1", "msg-1", "fake-model");
            await Task.CompletedTask;
        }
    }
}
