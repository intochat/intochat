using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Support;
using Microsoft.Playwright;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Tests.Browser;

/// <summary>
/// Item #350 (INT-2507) Nightly Playwright &amp; Real-Host E2E Journeys covering the Unified Product Reset:
/// <list type="number">
/// <item>Journey 1 — Unified Ino Flow (Waves 1A + 2A): 5-app Dock cleanup, CodeOrchestration JobCard, inline SplitPane drawer (Preview | Diff | Logs), ActiveContextChip + follow-up + clear, Pin as App (CrystallizeAsync in IntoChat.OS.Sandbox), and docked multi-option ChoiceCard (ITasks.Ask / AnswerAsk via root '/' target).</item>
/// <item>Journey 2 — Ino Voice &amp; Multimodal Attachments (Wave 1B): /ai/talk WebSocket auth gate (unauthenticated, wrong Origin, ?token=, single-use ticket + replay rejection), POST /ai/attachments validation (spoofed MIME/magic-bytes, oversized payload, valid PNG + WAV voice note), ConversationView rendering, and ISecretRedactor redaction.</item>
/// <item>Journey 3 — Friends &amp; Multi-Person Group Room (Waves 3A + 3B): 1:1 DM and multi-participant Group Room creation/switching, distinct Self/Human/@Ino speaker attribution, 0 agent turns on normal friend messages, room-scoped @Ino on explicit mention (zero private host contexts/tools + secret redaction), [Shareable] RoomChecklistApp pinning + IRoom.Act attribution + Role.Viewer rejection, and immediate Unfriend/Leave revocation.</item>
/// </list>
/// </summary>
[Collection(nameof(Item350WebShellCollection))]
public sealed class Item350UnifiedProductResetBrowserFacts(Item350WebShellFixture host)
{
    private const int ViewportWidth = 1440;
    private const int ViewportHeight = 900;

    private const string SafeTipCalculatorScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                await os.Notifications.NotifyAsync("Tip Calculator", "Ready", NotificationPriority.Normal, ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Tip Calculator compiled and verified in sandbox.",
                    Artifacts: [new OrchestrationArtifact("tip-calculator.json", "json", "{}")],
                    Metrics: new Dictionary<string, double> { ["checks"] = 1 });
            }
        }
        """;

    [Fact]
    public async Task Journey1_Unified_Ino_Flow_DockCleanup_InlineDrawer_ActiveContextChip_PinAsApp_And_DockedChoiceCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var httpErrors = TrackHttpErrors(page);

        try
        {
            // 1. Default Dock cleanup: verify DesktopApp's published view + live Desktop Shell in Chromium
            //    expose the 4 default apps (Ino, Files, GridCraft, Settings) and exclude retired Friends, Creator, Engineer, and Tasks.
            using (var kernel = host.CreateKernelClient())
            {
                var desktopViewJson = await kernel.GetStringAsync($"/brains/{host.Scope}/apps/desktop/view", ct);
                foreach (var requiredDockKey in new[] { "dock-ino", "dock-files", "dock-gridcraft", "dock-settings" })
                {
                    Assert.Contains($"\"{requiredDockKey}\"", desktopViewJson, StringComparison.Ordinal);
                }

                foreach (var excludedDockKey in new[] { "dock-friends", "dock-creator", "dock-engineer", "dock-tasks" })
                {
                    Assert.DoesNotContain($"\"{excludedDockKey}\"", desktopViewJson, StringComparison.Ordinal);
                }
            }

            await page.GotoAsync(host.ShellFor("apps/desktop/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.Locator("flt-semantics-host").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "All Apps", ct);

            // 2. Run a real CodeOrchestrationSynapse flow through the real Roslyn AST sanitizer + sandbox bridge
            //    and link it to a real ITasks record so Ino surfaces the JobCard ("Open artifact") + docked ChoiceCard.
            var brainId = new BrainId(host.Scope);
            var flowKey = $"item350-tipcalc-{Guid.NewGuid():N}"[..24];
            var orchId = new NeuronId(brainId, CodeOrchestrationSynapse.Alias, flowKey);
            string taskId;

            using (host.StampOwner())
            {
                var orch = SiloNeurons.Get<ICodeOrchestration>(host.Services, orchId);
                var started = await orch.RunAsync(
                    title: "Tip Calculator",
                    goal: "Build an interactive tip calculator",
                    allowedContracts: ["notifications"],
                    preauthoredScript: SafeTipCalculatorScript,
                    ct: ct);
                Assert.NotNull(started);

                var completedView = await WaitForCodeOrchestrationDoneAsync(orch, ct);
                Assert.Equal(FlowStatus.Completed, completedView.Status);

                var tasks = SiloNeurons.Get<ITasks>(host.Services, new NeuronId(brainId, "tasks", ITasks.DefaultKey));
                var createdTask = await tasks.Create(
                    title: "Build Tip Calculator",
                    description: "Verify inline drawer, Pin as App, and docked ChoiceCard",
                    tag: "App",
                    flowId: flowKey,
                    allowedContracts: ["notifications"]);
                taskId = createdTask.TaskId;
                await tasks.LinkArtifact(taskId, new TaskArtifactLink("code-orchestration", "Tip Calculator", $"apps/tip-calculator/view"));
                await tasks.Ask(taskId, new TaskAskPrompt(
                    Question: "Which environment should I deploy Tip Calculator to?",
                    Options: ["Sandbox", "Staging"],
                    RecommendedIndex: 0));
            }

            // 3. Switch InoApp to "main" (order-independent across journeys), open InoApp in Chromium, and verify JobCard + ChoiceCard render live.
            using (var kernel = host.CreateKernelClient())
            {
                using var switchMain = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.switch-conversation",
                        args = new { conversationId = "main" },
                    },
                    ct);
                Assert.True(switchMain.IsSuccessStatusCode, $"Failed to switch InoApp to 'main': {(int)switchMain.StatusCode}");
            }

            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);

            var openArtifactBtn = page.GetByRole(AriaRole.Button, new() { Name = "Open artifact" }).First;
            await openArtifactBtn.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "Which environment should I deploy Tip Calculator to?", ct);

            // 4. Click "Sandbox" option on the docked multi-option ChoiceCard (routes through root '/' target to InoApp -> ITasks.AnswerAsk).
            //    Resolving the docked ChoiceCard restores the composer so ActiveContextChip ("Editing: ...") can render above it.
            var choiceOptionKey = $"task-ask:{taskId}:0";
            var keyedChoice = page.KeyedNode(choiceOptionKey).First;
            if (await keyedChoice.CountAsync().WaitAsync(ct) > 0)
            {
                await page.ClickButtonAsync(choiceOptionKey, ct);
            }
            else
            {
                var sandboxOption = page.GetByRole(AriaRole.Button, new() { Name = "Sandbox" }).First;
                await sandboxOption.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
                await sandboxOption.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);
            }

            await WaitForSemanticsNotContainsAsync(page, "Which environment should I deploy Tip Calculator to?", ct);

            using (host.StampOwner())
            {
                var tasks = SiloNeurons.Get<ITasks>(host.Services, new NeuronId(brainId, "tasks", ITasks.DefaultKey));
                var resolved = await tasks.Read(taskId);
                Assert.NotNull(resolved);
                Assert.Equal(TaskStatus.InProgress, resolved!.Status);
                Assert.Null(resolved.PendingAsk);
                Assert.Equal("Sandbox", resolved.LastAnswer?.SelectedOption);
            }

            // 5. Click "Open artifact" -> opens inline right-hand SplitPane drawer with Preview | Diff | Logs tabs
            //    and renders ActiveContextChip ("Editing: Tip Calculator") above the composer.
            await openArtifactBtn.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode(InoView.DrawerTabPreviewKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20_000 }).WaitAsync(ct);
            await page.KeyedNode(InoView.DrawerTabDiffKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode(InoView.DrawerTabLogsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "Editing: Tip Calculator", ct);

            // Switch drawer tabs: Diff -> Logs -> Preview
            await page.ClickButtonAsync(InoView.DrawerTabDiffKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "OrchestrationScript", ct);

            await page.ClickButtonAsync(InoView.DrawerTabLogsKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "IntoChat.OS.Sandbox", ct);

            await page.ClickButtonAsync(InoView.DrawerTabPreviewKey, ct);

            // 6. Plain-English follow-up iteration while ActiveContextChip is active attaches [Active context: Tip Calculator (...)]
            using (var kernel = host.CreateKernelClient())
            {
                using var sendFollowUp = await kernel.PostAsJsonAsync(
                    $"/brains/{host.Scope}/act",
                    new
                    {
                        neuron = $"{host.Scope}/apps/ino",
                        target = ".",
                        signal = "ino.send",
                        args = new { message = "add a reset button" },
                    },
                    ct);
                Assert.True(sendFollowUp.IsSuccessStatusCode, $"Failed to send follow-up prompt: {(int)sendFollowUp.StatusCode}");
            }

            await WaitForSemanticsTextContainsAsync(page, "add a reset button", ct);
            using (host.StampOwner())
            {
                var conv = SiloNeurons.Get<IConversation>(
                    host.Services,
                    new NeuronId(brainId, IConversation.Type, IConversation.KeyOf(ConversationId.Parse("main"))));
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                var foundContextual = false;
                while (DateTimeOffset.UtcNow < deadline && !foundContextual)
                {
                    var convView = await conv.Read();
                    foundContextual = convView.Entries.OfType<Said>().Any(static s =>
                        s.Text.Contains("[Active context: Tip Calculator", StringComparison.Ordinal)
                        && s.Text.Contains("add a reset button", StringComparison.Ordinal));
                    if (!foundContextual)
                    {
                        await Task.Delay(100, ct);
                    }
                }

                Assert.True(foundContextual, "Expected plain-English follow-up to attach [Active context: Tip Calculator (...)] in IConversation.");
            }

            // 7. Click "Pin as App" -> runs ICodeOrchestration.CrystallizeAsync in IntoChat.OS.Sandbox,
            //    updates button to "Open Pinned App" and enables "Pop out".
            await page.ClickButtonAsync(InoView.DrawerPinKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Open Pinned App", ct);
            await WaitForSemanticsTextContainsAsync(page, "Pinned as App", ct);

            var popout = page.KeyedNode(InoView.DrawerPopoutKey).First;
            await popout.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var popoutDisabled = await popout.GetAttributeAsync("aria-disabled").WaitAsync(ct);
            Assert.NotEqual("true", popoutDisabled);
            Assert.Contains(host.Sandbox.ScopedDeployments, d =>
                string.Equals(d.BrainId, host.Scope, StringComparison.Ordinal)
                && d.AppKey.StartsWith("pinned-", StringComparison.Ordinal));

            // 8. Click the × button on ActiveContextChip ("Clear active editing context") -> clears Editing: Tip Calculator
            var clearChip = page.GetByRole(AriaRole.Button, new() { Name = "Clear active editing context" }).First;
            await clearChip.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await clearChip.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);
            await WaitForSemanticsNotContainsAsync(page, "Editing: Tip Calculator", ct);
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 1 (Unified Ino Flow)", page, httpErrors, ex, ct);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task Journey2_Ino_Voice_AuthGate_And_Multimodal_Attachments_With_SecretRedaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var convId = $"j2-{Guid.NewGuid():N}"[..16];

        // 1. /ai/talk WebSocket auth gate against the real ASP.NET Core host (via Origin proxy & Kernel)
        // 1a. Unauthenticated upgrade rejected (401)
        using (var unauthWs = new ClientWebSocket())
        {
            unauthWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() =>
                unauthWs.ConnectAsync(BuildTalkWsUri(host.KernelOrigin, convId), ct));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthWs.HttpStatusCode);
        }

        // 1b. Wrong Origin rejected (403) even with valid presence header
        using (var evilOriginWs = new ClientWebSocket())
        {
            evilOriginWs.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
            evilOriginWs.Options.SetRequestHeader("Origin", "https://attacker.invalid");
            evilOriginWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() =>
                evilOriginWs.ConnectAsync(BuildTalkWsUri(host.KernelOrigin, convId), ct));
            Assert.Equal(HttpStatusCode.Forbidden, evilOriginWs.HttpStatusCode);
        }

        // 1c. Query-string ?token= rejected (401) even with valid presence header
        using (var queryTokenWs = new ClientWebSocket())
        {
            queryTokenWs.Options.SetRequestHeader(PresenceProof.HeaderName, host.PresenceToken);
            queryTokenWs.Options.CollectHttpResponseDetails = true;
            var uriWithToken = new UriBuilder(BuildTalkWsUri(host.KernelOrigin, convId))
            {
                Query = $"conversation={Uri.EscapeDataString(convId)}&token=forbidden-qs-secret",
            }.Uri;
            _ = await Assert.ThrowsAsync<WebSocketException>(() => queryTokenWs.ConnectAsync(uriWithToken, ct));
            Assert.Equal(HttpStatusCode.Unauthorized, queryTokenWs.HttpStatusCode);
        }

        // 1d. POST /ai/talk/ticket mints single-use ticket -> succeeds once, rejects replay (401)
        string ticket;
        using (var kernel = host.CreateKernelClient())
        {
            using var ticketResp = await kernel.PostAsJsonAsync("/ai/talk/ticket", new { conversation = convId }, ct);
            Assert.Equal(HttpStatusCode.OK, ticketResp.StatusCode);
            using var doc = JsonDocument.Parse(await ticketResp.Content.ReadAsStringAsync(ct));
            ticket = doc.RootElement.GetProperty("ticket").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(ticket));
            Assert.Equal(30, doc.RootElement.GetProperty("expiresInSeconds").GetInt32());
        }

        var ticketUri = BuildTalkWsUri(host.KernelOrigin, convId, ticket);
        using (var validWs = new ClientWebSocket())
        {
            await validWs.ConnectAsync(ticketUri, ct);
            Assert.Equal(WebSocketState.Open, validWs.State);
            await validWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }

        using (var replayWs = new ClientWebSocket())
        {
            replayWs.Options.CollectHttpResponseDetails = true;
            _ = await Assert.ThrowsAsync<WebSocketException>(() => replayWs.ConnectAsync(ticketUri, ct));
            Assert.Equal(HttpStatusCode.Unauthorized, replayWs.HttpStatusCode);
        }

        // 2. Multimodal attachment upload validation against real POST /ai/attachments
        AttachmentRef uploadedPng;
        AttachmentRef uploadedVoice;
        using (var kernel = host.CreateKernelClient())
        {
            // 2a. Spoofed MIME / magic bytes rejected (415 Unsupported Media Type): declares image/png with HTML bytes
            using (var spoofedForm = BuildMultipartUpload("spoofed.png", "image/png", Encoding.UTF8.GetBytes("<html>not a png</html>")))
            using (var spoofedResp = await kernel.PostAsync($"/ai/attachments?conversation={Uri.EscapeDataString(convId)}", spoofedForm, ct))
            {
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, spoofedResp.StatusCode);
            }

            // 2b. Oversized voice-note payload rejected (413): > 10 MiB WAV audio
            var oversizedWav = BuildValidWavBytes(10 * 1024 * 1024 + 4096);
            using (var oversizedForm = BuildMultipartUpload("huge-voice.wav", "audio/wav", oversizedWav))
            using (var oversizedResp = await kernel.PostAsync($"/ai/attachments?kind=voice-note&transcribe=false&conversation={Uri.EscapeDataString(convId)}", oversizedForm, ct))
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResp.StatusCode);
            }

            // 2c. Valid PNG image upload succeeds (200)
            using (var pngForm = BuildMultipartUpload("architecture-diagram.png", "image/png", MinimalValidPngBytes()))
            using (var pngResp = await kernel.PostAsync($"/ai/attachments?conversation={Uri.EscapeDataString(convId)}", pngForm, ct))
            {
                Assert.Equal(HttpStatusCode.OK, pngResp.StatusCode);
                uploadedPng = (await pngResp.Content.ReadFromJsonAsync<AttachmentRef>(JsonSerializerOptions.Web, ct))!;
                Assert.Equal("image", uploadedPng.Kind);
                Assert.Equal("architecture-diagram.png", uploadedPng.FileName);
            }

            // 2d. Valid WAV voice-note upload succeeds (200) and ISecretRedactor masks the canary secret in transcript
            using var wavForm = BuildMultipartUpload("field-note.wav", "audio/wav", BuildValidWavBytes(2048));
            using var wavResp = await kernel.PostAsync($"/ai/attachments?kind=voice-note&durationMs=2400&conversation={Uri.EscapeDataString(convId)}", wavForm, ct);
            Assert.Equal(HttpStatusCode.OK, wavResp.StatusCode);
            uploadedVoice = (await wavResp.Content.ReadFromJsonAsync<AttachmentRef>(JsonSerializerOptions.Web, ct))!;
            Assert.Equal("voice-note", uploadedVoice.Kind);
            Assert.NotNull(uploadedVoice.Transcript);
            Assert.Contains("****", uploadedVoice.Transcript, StringComparison.Ordinal);
            Assert.DoesNotContain(Item350WebShellFixture.SyntheticSecretValue, uploadedVoice.Transcript, StringComparison.Ordinal);
        }

        // 3. Switch InoApp to a fresh conversation for Journey 2, submit message with uploaded image + voice-note attachments
        //    and secret-bearing text into the real IConversation, then verify ConversationView renders in Chromium with secret redacted.
        using (var kernel = host.CreateKernelClient())
        {
            using var switchResp = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/act",
                new
                {
                    neuron = $"{host.Scope}/apps/ino",
                    target = ".",
                    signal = "ino.switch-conversation",
                    args = new { conversationId = convId },
                },
                ct);
            Assert.True(switchResp.IsSuccessStatusCode, $"Failed to switch InoApp to fresh conversation '{convId}': {(int)switchResp.StatusCode}");
        }

        var brainId = new BrainId(host.Scope);
        using (host.StampOwner())
        {
            var conversation = SiloNeurons.Get<IConversation>(
                host.Services,
                new NeuronId(brainId, IConversation.Type, IConversation.KeyOf(ConversationId.Parse(convId))));
            await conversation.Say(
                $"Attached diagram and voice note with secret {Item350WebShellFixture.SyntheticSecretValue}",
                model: null,
                attachments: [uploadedPng, uploadedVoice]);
        }

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var httpErrors = TrackHttpErrors(page);

        try
        {
            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.ChildNode("conversation").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await WaitForSemanticsTextContainsAsync(page, "architecture-diagram.png", ct);
            await WaitForSemanticsTextContainsAsync(page, "Attached diagram and voice note with secret ****", ct);
            await WaitForSemanticsTextContainsAsync(page, "Play voice note", ct);

            // Expand VoiceClipCard's collapsible Transcript section and verify redacted transcript renders
            var transcriptBtn = page.GetByRole(AriaRole.Button, new() { Name = "Transcript" }).First;
            await transcriptBtn.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await transcriptBtn.ClickAsync(new() { Force = true, Timeout = 15_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "Voice note transcript with sensitive key **** redacted safely.", ct);

            var fullSemantics = await ReadAllSemanticsTextAsync(page, ct);
            Assert.DoesNotContain(Item350WebShellFixture.SyntheticSecretValue, fullSemantics, StringComparison.Ordinal);
            Assert.Contains("****", fullSemantics, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 2 (Voice & Multimodal Attachments)", page, httpErrors, ex, ct);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task Journey3_Friends_MultiRoom_SpeakerAttribution_RoomScopedIno_ShareableChecklist_And_Revocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var hostBrain = new BrainId(host.Scope);
        var bobBrain = new BrainId(DigitalBrain.Brains.BrainScope.Create("bob", "bob").Id);
        var carolBrain = new BrainId(DigitalBrain.Brains.BrainScope.Create("carol", "carol").Id);
        host.ProvisionPersonalBrain("bob", bobBrain.Value);
        host.ProvisionPersonalBrain("carol", carolBrain.Value);

        // 1. Confirm friendships between host ("local") and two friends ("bob" as Editor, "carol" as Viewer) using real IFriends grains.
        await EstablishFriendshipAsync(hostBrain, IntoChat.OS.Modules.Identity.Accounts.AccountSession.DefaultLogin, bobBrain, "bob");
        await EstablishFriendshipAsync(hostBrain, IntoChat.OS.Modules.Identity.Accounts.AccountSession.DefaultLogin, carolBrain, "carol");

        await using var context = await host.NewBrowserContextAsync(ViewportWidth, ViewportHeight, ct);
        var page = await context.NewPageAsync();
        var httpErrors = TrackHttpErrors(page);

        try
        {
            // 2. Create 1:1 DM with bob and multi-participant Group Room in Unified Messenger (app.ino, post-INT-2605 FriendsApp retirement).
            var dmShortKey = $"dm-bob-{Guid.NewGuid():N}"[..16];
            var groupShortKey = $"trip-squad-{Guid.NewGuid():N}"[..18];
            var groupRoomKey = $"conversations/{groupShortKey}";
            var groupRoomId = new NeuronId(hostBrain, IConversation.Type, groupRoomKey);

            using (host.StampOwner())
            {
                var roomsIndex = SiloNeurons.Get<IThreadDirectory>(host.Services, new NeuronId(hostBrain, Threads.Type, IThreadDirectory.DefaultKey));
                await roomsIndex.Create(dmShortKey, "DM with bob", ThreadTrustPosture.CrossBrainShared, ThreadKind.DirectPerson);
                await roomsIndex.Create(groupShortKey, "Weekend Trip Squad", ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom);

                var dmRoom = SiloNeurons.Get<IConversation>(host.Services, new NeuronId(hostBrain, IConversation.Type, $"conversations/{dmShortKey}"));
                await dmRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.DirectPerson, "DM with bob");
                await dmRoom.Invite(new AccountId("bob"), bobBrain, Role.Editor);

                var hostRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                await hostRoom.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Weekend Trip Squad");
                await hostRoom.Invite(new AccountId("bob"), bobBrain, Role.Editor);
                await hostRoom.Invite(new AccountId("carol"), carolBrain, Role.Viewer);
            }

            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "Weekend Trip Squad", ct);

            // 3. Switch between DM and Group Room in Chromium Unified Messenger Arc sidebar.
            await page.ClickButtonAsync($"session-room-{dmShortKey}", ct);
            await page.ClickButtonAsync($"session-room-{groupShortKey}", ct);
            await WaitForSemanticsTextContainsAsync(page, "CrossBrainShared · E2EE", ct);

            // 4. Normal friend messages trigger 0 agent turns; host message renders as Self, bob's renders with Human attribution
            host.Inference.ResetRoomCounters();

            using (host.StampOwner())
            {
                await SiloNeurons.Get<IConversation>(host.Services, groupRoomId).Say("Welcome to the trip room!");
            }

            using (host.StampPerson("bob", bobBrain.Value))
            {
                await SiloNeurons.Get<IConversation>(host.Services, groupRoomId).Say("Hey everyone, ready for the weekend trip?");
            }

            await WaitForSemanticsTextContainsAsync(page, "Hey everyone, ready for the weekend trip?", ct);
            await page.WaitForTimeoutAsync(400).WaitAsync(ct);
            Assert.Equal(0, host.Inference.RoomCallCount);

            // 5. Explicit @Ino mention triggers room-scoped @Ino (SharedRoomAgentPolicy: zero private host contexts/tools + secret redaction)
            using (host.StampPerson("bob", bobBrain.Value))
            {
                await SiloNeurons.Get<IConversation>(host.Services, groupRoomId)
                    .Say($"@Ino please summarize our plan without leaking {Item350WebShellFixture.SyntheticSecretValue}");
            }

            await WaitForSemanticsTextContainsAsync(page, "@Ino · Room only", ct);
            await WaitForSemanticsTextContainsAsync(page, "Room summary for everyone: weekend plan confirmed", ct);
            Assert.Equal(1, host.Inference.RoomCallCount);

            var roomRequest = Assert.Single(host.Inference.RoomRequests);
            Assert.Null(roomRequest.Tools);
            var systemPrompt = string.Concat(roomRequest.Messages
                .Where(static m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static m => m.Content)
                .OfType<AiText>()
                .Select(static t => t.Text));
            Assert.Contains("@Ino · Room only", systemPrompt, StringComparison.Ordinal);
            Assert.Contains("zero access to any participant's private brain", systemPrompt, StringComparison.OrdinalIgnoreCase);

            // Verify secret redaction in both the stored ConversationView and rendered Chromium DOM
            using (host.StampOwner())
            {
                var roomView = await SiloNeurons.Get<IConversation>(host.Services, groupRoomId).Read();
                var messages = roomView.Entries.OfType<Said>().ToArray();
                Assert.Contains(messages, m => m.Sender?.Kind == SenderKind.Self);
                Assert.Contains(messages, m => m.Sender?.Kind == SenderKind.Human && m.Sender.AccountId == "bob");
                Assert.Contains(messages, m => m.Sender?.Kind == SenderKind.Agent && m.Sender.RoomScoped);
                Assert.All(messages, m =>
                    Assert.DoesNotContain(Item350WebShellFixture.SyntheticSecretValue, m.Text, StringComparison.Ordinal));
            }

            var renderedRoomText = await ReadAllSemanticsTextAsync(page, ct);
            Assert.DoesNotContain(Item350WebShellFixture.SyntheticSecretValue, renderedRoomText, StringComparison.Ordinal);
            Assert.Contains("bob", renderedRoomText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("@Ino · Room only", renderedRoomText, StringComparison.Ordinal);

            // 6. [Shareable] RoomChecklistApp pinning + IConversation.Act live updates with Caller.For.Account attribution + Role.Viewer rejection
            const string checklistKey = "default";
            using (host.StampOwner())
            {
                var hostRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                // Non-shareable private host screens must be refused
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hostRoom.Pin("app.settings", "default"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hostRoom.Pin("app.ino", "default"));

                await hostRoom.Pin("room.checklist", checklistKey);
            }

            // Open Pinned Apps drawer in Unified Messenger and verify live RoomChecklistApp renders
            await page.ClickButtonAsync(InoArcSidebar.OpenPinnedAppsKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Shared Checklist", ct);

            // Bob (Role.Editor) acts on the pinned RoomChecklistApp via IConversation.Act -> attributes Caller.For.Account ("bob")
            using (host.StampPerson("bob", bobBrain.Value))
            {
                var bobRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                var updatedView = await bobRoom.Act(
                    "room.checklist",
                    checklistKey,
                    new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Pack tent" })));
                Assert.NotNull(updatedView);
                var checklistJson = JsonSerializer.Serialize(updatedView, JsonSerializerOptions.Web);
                Assert.Contains("Pack tent", checklistJson, StringComparison.Ordinal);
                Assert.Contains("last updated by bob", checklistJson, StringComparison.Ordinal);
                Assert.Contains("by bob", checklistJson, StringComparison.Ordinal);
            }

            // Carol (Role.Viewer) is refused on IConversation.Act and IConversation.Say, while allowed to Read/Here
            using (host.StampPerson("carol", carolBrain.Value))
            {
                var carolRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                await carolRoom.Here();
                var carolView = await carolRoom.Read();
                Assert.NotEmpty(carolView.Entries.OfType<Said>());

                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    carolRoom.Act("room.checklist", checklistKey, new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Unauthorized item" }))));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    carolRoom.Say("Viewer should not be able to send"));
            }

            // 7. Immediate Leave & Unfriend revocation
            // 7a. Bob leaves the group room -> immediately refused on Read/Say/Act/Here
            using (host.StampPerson("bob", bobBrain.Value))
            {
                var bobRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                await bobRoom.Leave();
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Read());
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Say("after leave"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bobRoom.Act("room.checklist", checklistKey, new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "After leave" }))));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(bobRoom.Here);
            }

            // 7b. Host unfriends carol -> carol is immediately refused on Read/Here
            using (host.StampOwner())
            {
                var hostFriends = SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(hostBrain.Value));
                await hostFriends.Unfriend(new AccountId("carol"));
            }

            using (host.StampPerson("carol", carolBrain.Value))
            {
                var carolRoom = SiloNeurons.Get<IConversation>(host.Services, groupRoomId);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carolRoom.Read());
                await Assert.ThrowsAsync<UnauthorizedAccessException>(carolRoom.Here);
            }
        }
        catch (Exception ex)
        {
            throw await BuildDiagnosticFailureAsync("Journey 3 (Friends & Multi-Person Group Room)", page, httpErrors, ex, ct);
        }

        Assert.Empty(httpErrors);
    }

    private async Task EstablishFriendshipAsync(BrainId hostBrain, string hostAccount, BrainId friendBrain, string friendAccount)
    {
        Code code;
        using (host.StampPerson(hostAccount, hostBrain.Value))
        {
            var hostFriends = SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(hostBrain.Value));
            code = await hostFriends.Mint();
        }

        using (host.StampPerson(friendAccount, friendBrain.Value))
        {
            var friendFriends = SiloNeurons.Get<IFriends>(host.Services, FriendsDefaults.Id(friendBrain.Value));
            _ = await friendFriends.Redeem(code);
        }
    }

    private static async Task<CodeOrchestrationView> WaitForCodeOrchestrationDoneAsync(ICodeOrchestration orch, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var view = await orch.ReadAsync(ct);
            if (view is not null && view.Status is FlowStatus.Completed or FlowStatus.Failed)
            {
                return view;
            }

            await Task.Delay(50, ct);
        }

        throw new Xunit.Sdk.XunitException("Timed out waiting for CodeOrchestrationSynapse to complete.");
    }

    private static Uri BuildTalkWsUri(string httpOrigin, string conversation, string? ticket = null)
    {
        var query = ticket is null
            ? $"conversation={Uri.EscapeDataString(conversation)}"
            : $"conversation={Uri.EscapeDataString(conversation)}&ticket={Uri.EscapeDataString(ticket)}";
        return new UriBuilder(httpOrigin)
        {
            Scheme = "ws",
            Path = "/ai/talk",
            Query = query,
        }.Uri;
    }

    private static MultipartFormDataContent BuildMultipartUpload(string fileName, string mediaType, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static byte[] MinimalValidPngBytes()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    private static byte[] BuildValidWavBytes(int totalBytes)
    {
        var size = Math.Max(44, totalBytes);
        var wav = new byte[size];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), size - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16); // PCM chunk size
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (short)1); // PCM format
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (short)1); // mono
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), 16_000); // sample rate
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), 32_000); // byte rate
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (short)2); // block align
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (short)16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), size - 44);
        return wav;
    }

    private static List<string> TrackHttpErrors(IPage page, bool ignoreRoomChecklistEmbed403 = false)
    {
        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                var path = new Uri(response.Url).AbsolutePath;
                if (ignoreRoomChecklistEmbed403 && response.Status == 403 && path.EndsWith("/apps/node", StringComparison.Ordinal))
                {
                    return;
                }

                httpErrors.Add($"{response.Status} {path}");
            }
        };
        page.PageError += (_, error) => httpErrors.Add($"pageerror: {error}");
        page.Console += (_, msg) =>
        {
            if (string.Equals(msg.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                if (ignoreRoomChecklistEmbed403 && msg.Text.Contains("403", StringComparison.Ordinal))
                {
                    return;
                }

                httpErrors.Add($"console.error: {msg.Text}");
            }
        };
        return httpErrors;
    }

    private const string ReadSemanticsJs = """
        () => {
            const host = document.querySelector('flt-semantics-host');
            if (!host) return '';
            const parts = [];
            if (host.innerText) parts.push(host.innerText);
            for (const el of host.querySelectorAll('[aria-label]')) {
                const lbl = el.getAttribute('aria-label');
                if (lbl) parts.push(lbl);
            }
            return parts.join('\n');
        }
        """;

    private static Task<string> ReadAllSemanticsTextAsync(IPage page, CancellationToken ct)
        => page.EvaluateAsync<string>(ReadSemanticsJs).WaitAsync(ct);

    private static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        try
        {
            await page.WaitForFunctionAsync(
                """
                expected => {
                    const host = document.querySelector('flt-semantics-host');
                    if (!host) return false;
                    const parts = [];
                    if (host.innerText) parts.push(host.innerText);
                    for (const el of host.querySelectorAll('[aria-label]')) {
                        const lbl = el.getAttribute('aria-label');
                        if (lbl) parts.push(lbl);
                    }
                    return parts.join('\n').toLowerCase().includes(expected.toLowerCase());
                }
                """,
                expectedSubstring,
                new PageWaitForFunctionOptions { Timeout = 25_000 }).WaitAsync(ct);
            return;
        }
        catch (TimeoutException)
        {
        }

        var finalText = await ReadAllSemanticsTextAsync(page, ct);
        Assert.Contains(expectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForSemanticsNotContainsAsync(IPage page, string unexpectedSubstring, CancellationToken ct)
    {
        try
        {
            await page.WaitForFunctionAsync(
                """
                unexpected => {
                    const host = document.querySelector('flt-semantics-host');
                    if (!host) return true;
                    const parts = [];
                    if (host.innerText) parts.push(host.innerText);
                    for (const el of host.querySelectorAll('[aria-label]')) {
                        const lbl = el.getAttribute('aria-label');
                        if (lbl) parts.push(lbl);
                    }
                    return !parts.join('\n').toLowerCase().includes(unexpected.toLowerCase());
                }
                """,
                unexpectedSubstring,
                new PageWaitForFunctionOptions { Timeout = 20_000 }).WaitAsync(ct);
            return;
        }
        catch (TimeoutException)
        {
        }

        var finalText = await ReadAllSemanticsTextAsync(page, ct);
        Assert.DoesNotContain(unexpectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Xunit.Sdk.XunitException> BuildDiagnosticFailureAsync(
        string journeyName,
        IPage page,
        List<string> httpErrors,
        Exception ex,
        CancellationToken ct)
    {
        var visible = await ReadAllSemanticsTextAsync(page, ct);
        var ids = await page.ReadSemanticsIdentifiersAsync(ct);
        return new Xunit.Sdk.XunitException(
            $"{journeyName} failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(100))}]\nVisible text:\n{visible[..Math.Min(2500, visible.Length)]}",
            ex);
    }
}
