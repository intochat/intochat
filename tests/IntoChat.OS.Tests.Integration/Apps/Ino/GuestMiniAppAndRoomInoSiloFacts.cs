using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

[Collection(nameof(InoSiloCollection))]
public sealed class GuestMiniAppAndRoomInoSiloFacts(InoSiloFixture fixture)
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task GuestInteractsLiveWithPinnedShareableChecklist_AndRoomInoTogglesViaRoomSafeActWithZeroPrivateLeakage()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrainName = fixture.NewBrain("alice-room");
        var bobBrainName = fixture.NewBrain("bob-guest");
        var aliceBrain = new BrainId(aliceBrainName);
        var bobBrain = new BrainId(bobBrainName);

        // 1. Establish friendship between Alice and Bob on the real silo.
        Code friendCode;
        using (fixture.AsPerson(aliceBrainName))
        {
            var aliceFriends = SiloNeurons.Get<IFriends>(fixture.Services, FriendsDefaults.Id(aliceBrainName));
            friendCode = await aliceFriends.Mint();
        }

        using (fixture.AsPerson(bobBrainName))
        {
            var bobFriends = SiloNeurons.Get<IFriends>(fixture.Services, FriendsDefaults.Id(bobBrainName));
            await bobFriends.Redeem(friendCode);
        }

        // 2. Alice creates a CrossBrainShared room, pins [Shareable] RoomChecklistApp, and invites Bob as Editor.
        var roomKey = "conversations/shared-trip";
        var roomId = new NeuronId(aliceBrain, IConversation.Type, roomKey);
        var checklistId = new NeuronId(aliceBrain, "room.checklist", "default");
        using (fixture.AsPerson(aliceBrainName))
        {
            var room = SiloNeurons.Get<IConversation>(fixture.Services, roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Shared Trip");
            await room.Pin("room.checklist", "default");
            await room.Invite(new AccountId(bobBrainName), bobBrain, Role.Editor);
            await room.Act("room.checklist", "default", new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Book flights" })));
        }

        var proxies = fixture.Services.GetRequiredService<INeuronProxies>();
        var hostedNeurons = fixture.Services.GetService<IHostedNeuronTypes>();
        var edge = fixture.Services.GetService<EdgeAuthority>();

        // 3. Bob (guest) reads the pinned checklist live via /apps/node AND adds an item via /act — preserving Bob's caller identity!
        using (fixture.AsPerson(bobBrainName))
        {
            var bobCaller = fixture.Services.GetRequiredService<ICallerAmbient>().Current;
            var guestRead = await AppUiEndpoints.ReadNode(
                UIVocabulary.ViewType,
                $"{aliceBrainName}/apps/room.checklist/view",
                proxies,
                bobBrainName,
                ct,
                caller: bobCaller,
                hostedNeurons: hostedNeurons,
                edge: edge);
            Assert.NotNull(guestRead);

            var guestAct = await AppUiEndpoints.Act(
                new AppUiEndpoints.ActRequest(
                    $"{aliceBrainName}/apps/room.checklist",
                    ".",
                    "room-checklist.add",
                    JsonSerializer.SerializeToElement(new { label = "Pack chargers" })),
                proxies,
                bobBrainName,
                ct,
                bobCaller,
                hostedNeurons: hostedNeurons,
                edge: edge);
            Assert.NotNull(guestAct);

            // Fail-closed: host-private app.settings is ALWAYS denied to Bob.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(
                    UIVocabulary.ViewType,
                    $"{aliceBrainName}/apps/app.settings/view",
                    proxies,
                    bobBrainName,
                    ct,
                    caller: bobCaller,
                    hostedNeurons: hostedNeurons,
                    edge: edge));
        }

        // 4. Verify checklist holds both Host and Bob's items with accurate per-caller attribution.
        using (fixture.AsPerson(aliceBrainName))
        {
            var checklistView = await SiloNeurons.Get<IScreen>(fixture.Services, checklistId).Read();
            var json = JsonSerializer.Serialize(checklistView);
            Assert.Contains("Book flights", json, StringComparison.Ordinal);
            Assert.Contains("Pack chargers", json, StringComparison.Ordinal);
            Assert.Contains($"by {bobBrainName}", json, StringComparison.Ordinal);
        }

        // 5. @Ino [RoomSafe] Act tool: when explicitly @mentioned in the shared room, @Ino toggles item i1 and has zero private context.
        var sawPrivateLeak = false;
        var toolRound = 0;
        fixture.Model.ClearRequests();
        fixture.Model.Respond((messages, options, _) =>
        {
            var allText = (options?.Instructions ?? "") + string.Concat(messages.Select(static m => m.Text ?? ""));
            if (allText.Contains("PRIVATE_SECRET", StringComparison.Ordinal))
            {
                sawPrivateLeak = true;
            }

            if (toolRound++ == 0)
            {
                var call = new FunctionCallContent(
                    "call-toggle-1",
                    SharedRoomAgentPolicy.RoomActToolName,
                    new Dictionary<string, object?>
                    {
                        ["screenType"] = "room.checklist",
                        ["screenKey"] = "default",
                        ["signal"] = "room-checklist.toggle",
                        ["args"] = new Dictionary<string, object?> { ["itemId"] = "i1", ["checked"] = true },
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Checked off 'Book flights' for the room!")));
        });

        using (fixture.AsPerson(aliceBrainName))
        {
            var room = SiloNeurons.Get<IConversation>(fixture.Services, roomId);
            await room.Say("@Ino please check off Book flights");

            var deadline = Stopwatch.StartNew();
            ConversationView view;
            do
            {
                view = await room.Read();
                if (view.Entries.OfType<Said>().Any(s => s.By is Participant.Assistant))
                {
                    break;
                }
                await Task.Delay(50, ct);
            }
            while (deadline.Elapsed < Within);

            Assert.Contains(view.Entries.OfType<Said>(), s => s.By is Participant.Assistant && s.Text.Contains("Book flights", StringComparison.Ordinal));
            Assert.False(sawPrivateLeak);

            var afterToggle = await SiloNeurons.Get<IScreen>(fixture.Services, checklistId).Read();
            var afterJson = JsonSerializer.Serialize(afterToggle);
            Assert.Contains("1/2 done", afterJson, StringComparison.Ordinal);
        }
    }
}
