using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

public sealed class MultiRoomAndRoomInoFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NeuronId IndexOf(Person person) => new(person.Brain, Threads.Type, IThreadDirectory.DefaultKey);

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task MultiRoomCreateListInviteLeaveAndReactiveSignalsWork(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);

        var aliceIndex = alice.Get<IThreadDirectory>(IndexOf(alice));
        var dmId = await aliceIndex.CreateShared("Bob", ThreadKind.DirectPerson, "dm-bob");
        var groupId = await aliceIndex.CreateShared("Weekend Trip", ThreadKind.SharedRoom, "trip-group");
        await alice.Get<IConversation>(dmId).Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.DirectPerson, "Bob");
        await alice.Get<IConversation>(dmId).Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await alice.Get<IConversation>(groupId).Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Weekend Trip");
        await alice.Get<IConversation>(groupId).Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await alice.Get<IConversation>(groupId).Invite(new AccountId(carol.Account), carol.Brain, Role.Viewer);

        var aliceRooms = (await aliceIndex.List(archived: false)).Entries
            .Where(static e => e.TrustPosture == ThreadTrustPosture.CrossBrainShared)
            .ToArray();
        Assert.Equal(2, aliceRooms.Length);
        Assert.Contains(aliceRooms, r => r.Thread == dmId && r.Kind == ThreadKind.DirectPerson && r.IsHost);
        Assert.Contains(aliceRooms, r => r.Thread == groupId && r.Kind == ThreadKind.SharedRoom && r.IsHost);

        var bobIndex = bob.Get<IThreadDirectory>(IndexOf(bob));
        var bobRooms = (await bobIndex.List(archived: false)).Entries
            .Where(static e => e.TrustPosture == ThreadTrustPosture.CrossBrainShared)
            .ToArray();
        Assert.Equal(2, bobRooms.Length);
        Assert.All(bobRooms, r => Assert.False(r.IsHost));
        Assert.Contains(bobRooms, r => r.Thread == dmId && r.Kind == ThreadKind.DirectPerson && r.Title == "Bob");
        Assert.Contains(bobRooms, r => r.Thread == groupId && r.Kind == ThreadKind.SharedRoom && r.Title == "Weekend Trip");

        await bob.Get<IConversation>(groupId).Leave();
        await bobIndex.Leave(groupId);
        var bobRoomsAfterLeave = (await bobIndex.List(archived: false)).Entries
            .Where(static e => e.TrustPosture == ThreadTrustPosture.CrossBrainShared)
            .ToArray();
        Assert.Single(bobRoomsAfterLeave);
        Assert.DoesNotContain(bobRoomsAfterLeave, r => r.Thread == groupId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.Get<IConversation>(groupId).Say("after leaving"));

        await using var checklist = await alice.Open<RoomChecklistApp>("collision-check", Ct);
        await checklist.Send(new AddChecklistItem("First"), Ct);
        await checklist.Send(new AddChecklistItem("Second"), Ct);
        await checklist.Send(new ToggleChecklistItem("i1", true), Ct);
        await checklist.Send(new ClearCompletedChecklistItems(), Ct);
        await checklist.Send(new AddChecklistItem("Third"), Ct);
        var checklistView = await alice.Get<IScreen>(new NeuronId(alice.Brain, "room.checklist", "collision-check")).Read();
        Assert.NotNull(checklistView);
        var raw = checklistView!.View.GetRawText();
        Assert.Contains("chk-i2", raw, StringComparison.Ordinal);
        Assert.Contains("chk-i3", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("chk-i1", raw, StringComparison.Ordinal);
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

        var aliceIndex = alice.Get<IThreadDirectory>(IndexOf(alice));
        var roomId = await aliceIndex.CreateShared("Design Sync", ThreadKind.SharedRoom, "design-sync");
        await alice.Get<IConversation>(roomId).Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Design Sync");
        await alice.Get<IConversation>(roomId).Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);

        await alice.Get<IConversation>(roomId).Say("Hey Bob, ready to review?");
        await bob.Get<IConversation>(roomId).Say("Yep, let's go!");
        await os.Idle();
        Assert.Equal(0, recording.CallCount);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => alice.Get<IConversation>(roomId).Say(new string('a', 32_001)));

        await bob.Get<IConversation>(roomId).Say("@Ino summarize our plan");
        await WaitUntilAsync(async () => recording.CallCount == 1 && (await alice.Get<IConversation>(roomId).Read()).Entries.OfType<Said>().Count() == 4, os.Idle, Ct);
        Assert.Equal(1, recording.CallCount);
        var lastRequest = Assert.Single(recording.Requests);
        Assert.Contains(lastRequest.Messages, m => m.Role == "system" && m.Content.OfType<AiText>().Any(t => t.Text.Contains("@Ino · Room only", StringComparison.Ordinal)));
        Assert.Contains(lastRequest.Messages, m => m.Sender?.AccountId == alice.Account);
        Assert.Contains(lastRequest.Messages, m => m.Sender?.AccountId == bob.Account);

        var aliceView = await alice.Get<IConversation>(roomId).Read();
        var aliceMessages = aliceView.Entries.OfType<Said>().ToArray();
        Assert.Equal(4, aliceMessages.Length);
        Assert.Equal(SenderKind.Self, aliceMessages[0].Sender?.Kind);
        Assert.Equal(alice.Account, aliceMessages[0].Sender?.AccountId);
        Assert.Equal(SenderKind.Human, aliceMessages[1].Sender?.Kind);
        Assert.Equal(bob.Account, aliceMessages[1].Sender?.AccountId);
        Assert.Equal(SenderKind.Agent, aliceMessages[3].Sender?.Kind);
        Assert.Equal(SharedRoomAgentPolicy.DefaultRoomDisplayName, aliceMessages[3].Sender?.DisplayName);

        var bobView = await bob.Get<IConversation>(roomId).Read();
        var bobMessages = bobView.Entries.OfType<Said>().ToArray();
        Assert.Equal(SenderKind.Human, bobMessages[0].Sender?.Kind);
        Assert.Equal(SenderKind.Self, bobMessages[1].Sender?.Kind);
        Assert.Equal(SenderKind.Agent, bobMessages[3].Sender?.Kind);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task RoomPinEnforcesShareableAndRoomActEnforcesEditorRoleWithCallerAttributionOnCheckBoxNeuron(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, _ => { }, Ct);
        var (alice, bob, carol) = (os.Person("alice"), os.Person("bob"), os.Person("carol"));
        await alice.Befriend(bob, Ct);
        await alice.Befriend(carol, Ct);

        var roomId = new NeuronId(alice.Brain, IConversation.Type, "conversations/checklist-room");
        var aliceRoom = alice.Get<IConversation>(roomId);
        await aliceRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Checklist Room");
        await aliceRoom.Invite(new AccountId(bob.Account), bob.Brain, Role.Editor);
        await aliceRoom.Invite(new AccountId(carol.Account), carol.Brain, Role.Viewer);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => aliceRoom.Pin("app.friends", "default"));

        const string checklistKey = "trip-checklist";
        await using var checklistSession = await alice.Open<RoomChecklistApp>(checklistKey, Ct);
        await aliceRoom.Pin("room.checklist", checklistKey);

        var hostAddCmd = new Command(".", "room-checklist.add", System.Text.Json.JsonSerializer.SerializeToElement(new { label = "Book flights" }));
        await aliceRoom.Act("room.checklist", checklistKey, hostAddCmd);

        var addCmd = new Command(".", "room-checklist.add", System.Text.Json.JsonSerializer.SerializeToElement(new { label = "Reserve hotel" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.Get<IConversation>(roomId).Act("room.checklist", checklistKey, addCmd));

        var afterAdd = await bob.Get<IConversation>(roomId).Act("room.checklist", checklistKey, addCmd);
        Assert.Contains($"last updated by {bob.Account}", afterAdd.View.GetRawText(), StringComparison.Ordinal);

        var toggleHostCheckBox = new Command(
            RoomChecklistApp.CheckKey("i1"),
            "control.setChecked",
            System.Text.Json.JsonSerializer.SerializeToElement(new { isOn = true }));
        var afterCheck = await aliceRoom.Act("room.checklist", checklistKey, toggleHostCheckBox);
        Assert.NotNull(afterCheck);
        await os.Idle();
        var latestView = await alice.Get<IScreen>(new NeuronId(alice.Brain, "room.checklist", checklistKey)).Read();
        Assert.NotNull(latestView);
        Assert.Contains("1/2 done", latestView!.View.GetRawText(), StringComparison.Ordinal);

        var toggleGuestCheckBox = new Command(
            RoomChecklistApp.CheckKey("i2"),
            "control.setChecked",
            System.Text.Json.JsonSerializer.SerializeToElement(new { isOn = true }));
        await aliceRoom.Act("room.checklist", checklistKey, toggleGuestCheckBox);
        await os.Idle();
        var afterGuestToggle = await alice.Get<IScreen>(new NeuronId(alice.Brain, "room.checklist", checklistKey)).Read();
        Assert.NotNull(afterGuestToggle);
        Assert.Contains("2/2 done", afterGuestToggle!.View.GetRawText(), StringComparison.Ordinal);

        var clearCmd = new Command(".", "room-checklist.clear-completed");
        await bob.Get<IConversation>(roomId).Act("room.checklist", checklistKey, clearCmd);
        await os.Idle();
        var afterClear = await alice.Get<IScreen>(new NeuronId(alice.Brain, "room.checklist", checklistKey)).Read();
        Assert.NotNull(afterClear);
        Assert.Contains("0/0 done", afterClear!.View.GetRawText(), StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, Func<Task> idle, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await idle();
            if (await condition()) { return; }
            await Task.Delay(25, ct);
        }
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
