using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoViewFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Human Owner = new(new AccountId("local"));
    private static readonly Participant.Assistant Ino = new("ino");

    private static ConversationView ViewOf(AgentRun? active = null, AgentRun[]? runs = null, params ConversationEntry[] entries)
        => new(ConversationId.Parse("main"), 1, [Owner, Ino], entries, active, runs ?? [], false, []);

    [Fact]
    public void AnEmptyConversationHasAnEmptyState()
    {
        var c = InoDrawing.Conversation(new InoAppState());
        Assert.Equal(("What do you need?", 0, true), (c.Empty!.Title, c.Messages.Count, c.Composer.Enabled));
    }

    [Fact]
    public void TheComposerIsOffWhileIAmWaitingForAReply()
        => Assert.False(InoDrawing.Conversation(new InoAppState() with { ActiveRunId = "r1" }).Composer.Enabled);

    [Fact]
    public void MyPendingMessageIsDrawnLastAndMarkedSending()
    {
        var view = ViewOf(entries:
        [
            new Said(EntryIds.Said("r0"), Owner, At, "earlier"),
            new Said(EntryIds.Reply("r0"), Ino, At, "ok"),
        ]);
        var s = InoModel.Sent(new InoAppState(), "hello", "p1", At);

        var c = InoDrawing.Conversation(s, view: view);
        Assert.Equal(("user", "hello", "sending"), (c.Messages[^1].Role, c.Messages[^1].Text, c.Messages[^1].Status));
        Assert.Contains("hello", Texts(InoDrawing.Screen(s, view: view)));
    }

    [Fact]
    public void AnErrorIsDrawnWithItsAction()
    {
        var c = InoDrawing.Conversation(new InoAppState() with { Error = new InoError("no-provider"), Status = InoModel.Failed });
        Assert.Equal(("Ino has no model to answer with.", "Set up a model"), (c.Error!.What, c.Error.Action!.Label));
    }

    [Fact]
    public void NoProviderWithDetectedLocalRtxModelOffersOneClickChoiceAndInlineKeySetup()
    {
        var view = ViewOf(
            runs: [new AgentRun("r1", AgentRunStatus.Failed, "Write a migration", At) { Failure = AgentFailure.NoProvider }],
            entries: [new Said(EntryIds.Said("r1"), Owner, At, "Write a migration")]);
        var s = new InoAppState() with
        {
            Error = new InoError("no-provider"),
            Status = InoModel.Failed,
            DetectedLocalModel = "qwen2.5-coder:14b",
            DetectedLocalModels = ["qwen2.5-coder:14b", "qwen3.5:4b"],
        };

        var screen = InoDrawing.Screen(s, view: view);
        var texts = Texts(screen);
        Assert.Contains("Ino has no model to answer with.", texts);
        Assert.Contains("Use local model (qwen2.5-coder:14b)", texts);

        // Model setup is Ino's own (its actions reach Ino): it is drawn under the conversation thread.
        var nodes = new PresentedView("main", screen).Nodes;
        var choiceCard = Assert.Single(nodes.OfType<ChoiceCardNode>());
        Assert.Contains(choiceCard.Options, o => o.Title == "Local Ollama · qwen2.5-coder:14b");
        var keyCard = Assert.Single(nodes.OfType<Card>());
        Assert.Contains(keyCard.Children, c => c is Field { Key: InoView.ProviderCredentialKey });
    }

    [Fact]
    public void StopIsOfferedOnlyWhileARunIsActive()
    {
        Assert.Null(InoDrawing.Conversation(new InoAppState()).Stop);
        Assert.NotNull(InoDrawing.Conversation(new InoAppState() with { ActiveRunId = "r1" }).Stop);
    }

    [Fact]
    public void OldTurnsAreNotDrawn()
    {
        var s = new InoAppState() with { Turns = [new InoTurn("old question", "old reply", "agent", 0)] };
        Assert.DoesNotContain("old reply", Texts(InoDrawing.Screen(s)));
    }

    [Fact]
    public void AViewOf512LongMessagesStaysUnderTheLimit()
    {
        var many = Enumerable.Range(0, 512)
            .Select(i => (ConversationEntry)(i % 2 == 0
                ? new Said(EntryIds.Said($"r{i}"), Owner, At, new string('x', 8_000))
                : new Said(EntryIds.Reply($"r{i - 1}"), Ino, At, new string('x', 8_000))))
            .ToArray();
        var view = ViewOf(entries: many);
        var s = new InoAppState();
        var thread = InoView.Thread(s, view);
        Assert.True(thread.Count <= InoView.MaxRenderedMessages);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(InoDrawing.Screen(s, view: view), JsonSerializerOptions.Web).Length < 2 * 1024 * 1024);
        Assert.Equal("r510/reply", thread[^1].Id);
    }

    [Fact]
    public void OneEnormousReplyIsCutNotDropped()
    {
        var view = ViewOf(entries: [new Said(EntryIds.Reply("r0"), Ino, At, new string('x', 3_000_000))]);
        Assert.Equal(InoView.MaxRenderedChars, InoView.Thread(new InoAppState(), view).Single().Text.Length);
    }

    [Theory]
    [InlineData("neuron://")]
    [InlineData("IntoChat.")]
    [InlineData("app.ino")]
    [InlineData("ino.send")]
    [InlineData("InoApp")]
    public void NoInternalVocabularyReachesThePerson(string forbidden)
    {
        foreach (var (s, v) in States.All)
        {
            var texts = Texts(InoDrawing.Screen(s, view: v));
            Assert.DoesNotContain(texts, t => t.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void InoSourceFilesContainZeroNullLoggerReferences()
    {
        var assemblyLocation = typeof(InoApp).Assembly.Location;
        var root = FindRepoRoot(Path.GetDirectoryName(assemblyLocation)!);
        var slnx = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var relProjectPath = slnx.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .First(p => p is not null && p.EndsWith("IntoChat.OS.Apps.csproj", StringComparison.OrdinalIgnoreCase))!;
        var appsDir = Path.GetDirectoryName(Path.Combine(root, relProjectPath.Replace('/', Path.DirectorySeparatorChar)))!;
        var inoDir = Path.Combine(appsDir, "Ino");
        var csFiles = Directory.GetFiles(inoDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);
        foreach (var file in csFiles)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("NullLogger", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TraceReaderRedactsGithubPatsAnthropicKeysBearerTokensUrlCredentialsAndPasswords()
    {
        var raw = string.Join(" | ",
            "token=" + string.Concat("github_pat_", "11ABCDEFG0123456789_secretXYZ"),
            "oauth=" + string.Concat("gho_", "abcdefghijklmnop123456"),
            "claude=" + string.Concat("sk-ant-api03-", "abcdefghijklmnopqrstuvwxyz012345"),
            "auth=Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.sig",
            "url=https://admin:superSecretPass@db.internal.example/crm",
            "conn=Host=db.local;Username=app;Password=myTopSecretDbPwd;Database=main");

        var redacted = TraceReaderNeuron.Redact(raw);

        Assert.DoesNotContain("11ABCDEFG0123456789_secretXYZ", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop123456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("superSecretPass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("myTopSecretDbPwd", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AspireLiveTelemetryDiscoversConfiguredEndpointsCapturesActivitiesAndSanitizesSecrets()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        AspireLiveTelemetry.EnsureListener();
        using var source = new ActivitySource("IntoChat.OS.InoIntrospectionTest");
        using (var activity = source.StartActivity("InoTurnProbe", ActivityKind.Internal))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Failed with Bearer secretTokenValue12345");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Modules:ai:Ollama:Endpoint"] = "http://user:pw123@127.0.0.1:11434",
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services)
            .Register(typeof(AspireModule).Assembly);
        var neuron = os.Get<IAspire>(new NeuronId(new BrainId("owner"), "aspire", "default"));
        var resources = await neuron.GetResourcesAsync();
        Assert.Contains(resources, r => r.Name == "ollama" && r.Endpoint == "http://127.0.0.1:11434" && !r.Endpoint.Contains("pw123", StringComparison.Ordinal));

        var traces = await neuron.GetTracesAsync("IntoChat.OS.InoIntrospectionTest", errorsOnly: true);
        var captured = Assert.Single(traces, t => t.Name == "InoTurnProbe");
        Assert.Equal("Error", captured.Status);
        Assert.DoesNotContain("secretTokenValue12345", captured.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolResultApprovalCardCarriesNoResolve()
    {
        var pending = Assert.IsType<ApprovalCardNode>(Assert.Single(InoView.RenderCardNodes(
            new InoCard("plan-42", "approval", "Open Draft PR", "Create draft PR on intochat/os", "f2pr", "pending", "owner"))));
        Assert.Null(pending.Resolve);
        Assert.Equal("brand:github", pending.Icon);
        Assert.Equal("GitHub", pending.CapabilityLabel);

        var approved = Assert.IsType<ApprovalCardNode>(Assert.Single(InoView.RenderCardNodes(
            new InoCard("plan-42", "approval", "Open Draft PR", "Create draft PR on intochat/os", "f2pr", "approved", "owner"))));
        Assert.Null(approved.Resolve);
        Assert.Equal("brand:github", approved.Icon);
        Assert.Equal("GitHub", approved.CapabilityLabel);

        var inboxApproval = Assert.IsType<ApprovalCardNode>(InoView.RenderApproval(
            new IntoChat.OS.Modules.Apps.Approvals.Approval(
                "plan-99",
                IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.FlowStep,
                "change-flow-99",
                "Push branch and open PR?",
                "Touches 2 files in repo os.",
                At,
                At.AddDays(7),
                "trace-99")));
        Assert.Null(inboxApproval.Resolve);
        Assert.Equal("brand:github", inboxApproval.Icon);
        Assert.Equal("GitHub", inboxApproval.CapabilityLabel);
    }

    [Fact]
    public void OnePlanHasOneApproveControlInTheInoConversation()
    {
        var approvalCard = new InoCard(
            "plan-42",
            "approval",
            "Waiting for your approval: Add Multiply (0123abcd)",
            "1 file(s) on ino/add-multiply (+1 / -0)",
            "neuron://local/runtime.confirmations/confirmations",
            "pending",
            "neuron://local/apps/tasks#change-flow-42");
        var view = ViewOf(entries:
        [
            new Said(EntryIds.Said("r1"), Owner, At, "Add Multiply"),
            new Returned(EntryIds.Result("r1", "c1"), Ino, At, "studio_propose_change",
                JsonSerializer.Serialize(new InoToolResult("Waiting.", approvalCard), JsonSerializerOptions.Web), true, null),
            new Said(EntryIds.Reply("r1"), Ino, At, "The change waits for your approval."),
        ]);
        var state = new InoAppState() with
        {
            Approvals =
            [
                new IntoChat.OS.Modules.Apps.Approvals.Approval(
                    "plan-42",
                    IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.FlowStep,
                    "change-flow-42",
                    "Add Multiply",
                    "approval-gate: Approving pushes one commit.",
                    At,
                    At.AddDays(2),
                    "trace-42"),
            ],
        };

        var screen = InoDrawing.Screen(state, view: view);
        var resolving = new PresentedView("main", screen).Nodes
            .OfType<ApprovalCardNode>()
            .Where(card => card.Resolve?.PlanId == "plan-42")
            .ToArray();

        Assert.Empty(resolving);
        var waiting = Assert.Single(new PresentedView("main", screen).Nodes.OfType<ApprovalCardNode>(), card => card.Id == "plan-42" && card.Key.StartsWith("approval-", StringComparison.Ordinal));
        Assert.Equal("plan-42", waiting.Id);
        Assert.Null(waiting.Resolve);
    }

    [Fact]
    public void ManifestHasNoVoiceOperationAndNoToolbar()
    {
        Assert.DoesNotContain(InoApp.Bundle.Manifest.Operations, static op => op.Name == "voice");
        Assert.Contains(InoApp.Bundle.Manifest.Operations, static op => op.Name == "chats");
        Assert.Null(InoApp.Bundle.Manifest.Toolbar);
    }

    [Fact]
    public void ConversationFormatsActivityTitlesAndModelBadgesUsingVisualCatalog()
    {
        var view = ViewOf(entries:
        [
            new Said(EntryIds.Said("r1"), Owner, At, "Inspect cluster and symbols"),
            new Returned(EntryIds.Result("r1", "a1"), Ino, At, "IAspire.GetResourcesAsync", "3 healthy", true, null),
            new Returned(EntryIds.Result("r1", "a2"), Ino, At, "aspire", "", true, null),
            new Returned(EntryIds.Result("r1", "a3"), Ino, At, "ino.studio", "", true, null),
            new Returned(EntryIds.Result("r1", "a4"), Ino, At, "IWorkTree", "", true, null),
            new Returned(EntryIds.Result("r1", "a5"), Ino, At, "ICodeGraph.SearchSymbols", "4 matches", true, null),
            new Said(EntryIds.Reply("r1"), Ino, At, "All green.", new ModelId("claude-sonnet-4-6")),
        ]);
        var state = new InoAppState() with { SelectedModel = "claude-sonnet-4-6" };

        var conversation = InoDrawing.Conversation(state, view: view);
        Assert.Equal("Sonnet 4.6", conversation.Composer.ActiveModelLabel);
        Assert.Equal("provider:anthropic", conversation.Composer.ActiveProviderIcon);

        var assistant = conversation.Messages[1];
        Assert.Equal("Sonnet 4.6", assistant.Model);
        Assert.Equal("Sonnet 4.6", assistant.ModelLabel);
        Assert.Equal("provider:anthropic", assistant.ProviderIcon);
        Assert.NotNull(assistant.Activities);
        Assert.Equal(
            [
                "Aspire",
                "Aspire",
                "Ino Studio",
                "GitHub",
                "CodeGraph",
            ],
            assistant.Activities!.Select(static a => a.Title).ToArray());
        Assert.Equal(
            [
                "Resources · 3 healthy",
                null,
                null,
                null,
                "Search Symbols · 4 matches",
            ],
            assistant.Activities!.Select(static a => a.Detail).ToArray());
        Assert.Equal(
            [
                "brand:aspire",
                "brand:aspire",
                "brand:intochat",
                "brand:github",
                "capability:codegraph",
            ],
            assistant.Activities!.Select(static a => a.Icon).ToArray());
        Assert.Equal(
            [
                "Developer",
                "Developer",
                "Studio",
                "Developer",
                "Developer",
            ],
            assistant.Activities!.Select(static a => a.Category).ToArray());

        var aspireVisual = InoView.ResolveActivityVisual(new InoActivity("a1", "IAspire.GetResourcesAsync"));
        Assert.Equal(("Aspire", "brand:aspire", "#512BD4"), (aspireVisual.Label, aspireVisual.IconKey, aspireVisual.AccentHex));

        var codeGraphVisual = InoView.ResolveActivityVisual(new InoActivity("a5", "ICodeGraph.SearchSymbols"));
        Assert.Equal(("CodeGraph", "capability:codegraph", "#10B981"), (codeGraphVisual.Label, codeGraphVisual.IconKey, codeGraphVisual.AccentHex));

        var model = InoView.ResolveModel("claude-sonnet-4-6");
        Assert.NotNull(model);
        Assert.Equal(("Sonnet 4.6", "provider:anthropic"), (model.Name, model.IconKey()));
    }

    [Fact]
    public void TheChipShowsTheCardsCleanName()
    {
        var props = InoView.ThreadProps(new InoAppState() with { SelectedModel = "IQwen35" });
        Assert.Equal("Qwen 3.5 4B", props.ActiveModelLabel);
        Assert.Equal("provider:ollama", props.ActiveProviderIcon);
        Assert.Equal("qwen3.5:4b", props.ActiveModelId);
    }

    [Fact]
    public void ChoicesSplitLocalAndCloudAndCarryTheReason()
    {
        var choices = InoView.Choices(
            [KnownModels.Qwen35, KnownModels.Opus5, KnownModels.Whisper1],
            [new(KnownModels.Qwen35.Id, true, null), new(KnownModels.Opus5.Id, false, "Add a key in API keys")]);

        Assert.Equal(["local", "cloud"], choices.Select(static x => x.Section));
        Assert.Equal("qwen3.5:4b", choices[0].Id);
        Assert.True(choices[0].Available);
        Assert.Null(choices[0].Note);
        Assert.Equal("Add a key in API keys", choices[1].Note);
        Assert.False(choices[1].Available);
        Assert.Contains("reasoning", choices[1].Abilities!);
    }

    [Fact]
    public void ADownloadingRowShowsItsPercent()
    {
        var device = new DeviceView(
            "m1",
            new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
            [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
            [new DeviceModel(KnownModels.Gemma4, RuntimeId.Ollama, InstallState.Downloading, Fit.Good)],
            [new Install(KnownModels.Gemma4.Id, RuntimeId.Ollama, InstallState.Downloading, 42, 420, 1000, null, null)]);

        var choices = InoView.Choices(
            [KnownModels.Gemma4],
            [new(KnownModels.Gemma4.Id, false, "Not installed")],
            device);

        Assert.Equal(("Downloading 42%", 0.42), (choices[0].Note, choices[0].Progress));
        Assert.False(choices[0].Available);
    }

    [Fact]
    public void ATooBigModelCannotBeChosen()
    {
        var device = new DeviceView(
            "m1",
            new Hardware(16L * 1024 * 1024 * 1024, 0, [Accelerator.Cpu]),
            [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
            [new DeviceModel(KnownModels.Qwen25Coder14B, RuntimeId.Ollama, InstallState.Available, Fit.WontFit)],
            []);

        var choices = InoView.Choices(
            [KnownModels.Qwen25Coder14B],
            [new(KnownModels.Qwen25Coder14B.Id, false, "Not installed")],
            device);

        Assert.Equal("Too big for this device", choices[0].Note);
        Assert.False(choices[0].Available);
        Assert.Null(choices[0].Progress);
    }

    [Fact]
    public void ANotInstalledDeviceModelShowsInstallSizeAndFit()
    {
        var card = KnownModels.Gemma4 with { Bytes = 2L * 1024 * 1024 * 1024 };
        var device = new DeviceView(
            "m1",
            new Hardware(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
            [new RuntimeStatus(RuntimeId.Ollama, true, "0.9.0", new Uri("http://127.0.0.1:11434"))],
            [new DeviceModel(card, RuntimeId.Ollama, InstallState.Available, Fit.Good)],
            []);

        var choices = InoView.Choices(
            [card],
            [new(card.Id, false, "Not installed")],
            device);

        Assert.False(choices[0].Available);
        Assert.Equal("Install · 2 GB · Good", choices[0].Note);
        Assert.Null(choices[0].Progress);
    }

    [Fact]
    public void ConversationFormatsNeuronsCallJsonIntoBrandActivityLabels()
    {
        var run = new AgentRun("r1", AgentRunStatus.Completed, "check health", At, Reply: "Healthy")
        {
            ToolCalls =
            [
                new AgentToolCall("call-1", "neurons_call",
                    """{"contract":"IntoChat.OS.Modules.Microsoft.Aspire.IAspire","key":"local/aspire","method":"GetResourcesAsync"}""",
                    "ok"),
                new AgentToolCall("call-2", "neurons_call",
                    """{"contract":"IntoChat.OS.Modules.ArtCraft.IArtCraftStudio","key":"local/artcraft/photocraft","method":"ExecuteCommand"}""",
                    "done"),
            ],
        };
        var view = ViewOf(
            runs: [run],
            entries:
            [
                new Said(EntryIds.Said("r1"), Owner, At, "check health"),
                new Returned(EntryIds.Result("r1", "call-1"), Ino, At, "neurons_call", "ok", true, null),
                new Returned(EntryIds.Result("r1", "call-2"), Ino, At, "neurons_call", "done", true, null),
                new Said(EntryIds.Reply("r1"), Ino, At, "Healthy", new ModelId("qwen2.5-coder:14b")),
            ]);

        var projected = InoModel.FromConversation(new InoAppState(), view);
        var conversation = InoDrawing.Conversation(projected, view: view);
        var assistant = conversation.Messages[1];

        Assert.Equal("Qwen 2.5 Coder 14B", assistant.Model);
        Assert.Equal("Qwen 2.5 Coder 14B", assistant.ModelLabel);
        Assert.Equal("provider:ollama", assistant.ProviderIcon);
        Assert.Equal(
            ["Aspire", "PhotoCraft"],
            assistant.Activities!.Select(static a => a.Title).ToArray());
        Assert.Equal(
            ["Resources · ok", "Execute Command · done"],
            assistant.Activities!.Select(static a => a.Detail).ToArray());
        Assert.Equal(
            ["brand:aspire", "artcraft:photocraft"],
            assistant.Activities!.Select(static a => a.Icon).ToArray());
    }

    [Fact]
    public void ComposerDictationDefaultsToFalseWhenOmitted()
    {
        var state = new InoAppState();

        var conversation = InoDrawing.Conversation(state);
        Assert.False(conversation.Composer.Dictation);

        var screen = InoDrawing.Screen(state);
        var rootConversation = Assert.IsType<Conversation>(Assert.Single(Assert.IsType<Column>(screen.Root).Children));
        Assert.False(rootConversation.Composer.Dictation);
    }

    [Fact]
    public void ComposerDictationReflectsTrueWhenEnabled()
    {
        var state = new InoAppState();

        var conversation = InoDrawing.Conversation(state, dictation: true);
        Assert.True(conversation.Composer.Dictation);

        var screen = InoDrawing.Screen(state, dictation: true);
        var rootConversation = Assert.IsType<Conversation>(Assert.Single(Assert.IsType<Column>(screen.Root).Children));
        Assert.True(rootConversation.Composer.Dictation);
    }

    [Fact]
    public void TheThreadAloneStillFlexes()
    {
        var root = Assert.IsType<Column>(InoDrawing.Screen(new InoAppState()).Root);
        Assert.Equal([1d], root.Extents);
        Assert.IsType<Conversation>(Assert.Single(root.Children));
    }

    [Fact]
    public void ThreadFillsWithCardsAround()
    {
        var state = new InoAppState() with
        {
            DraftPrompt = "Check deployment status",
            Status = InoModel.Failed,
            Error = new InoError("no-provider"),
            Approvals =
            [
                new IntoChat.OS.Modules.Apps.Approvals.Approval(
                    "plan-1",
                    IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.FlowStep,
                    "change-flow-1",
                    "Push branch?",
                    "Touches 1 file.",
                    At,
                    At.AddDays(1),
                    "trace-1"),
            ],
        };

        var root = Assert.IsType<Column>(InoDrawing.Screen(state).Root);
        Assert.Single(root.Extents!, e => e == 1d);
        Assert.Equal(root.Children.Count, root.Extents!.Count);
        Assert.Single(root.Children.OfType<Conversation>());
        Assert.DoesNotContain(root.Children, c => c is Column);
    }

    [Fact]
    public void SessionsSectionDrawsNewestConversationsFirstAboveTheThread()
    {
        var page = new SessionPage(
        [
            new ConversationSession("main", "Current chat", At, false),
            new ConversationSession("c-6c8edd77", "Bergen trip", At.AddMinutes(-5), false),
        ], 2);
        var state = new InoAppState() with { ActiveSection = "chats" };

        var screen = InoDrawing.Screen(state, sessions: page);
        var presented = new PresentedView("main", screen);
        var collection = Assert.Single(presented.Nodes.OfType<Collection>());
        Assert.Equal(["main", "c-6c8edd77"], collection.Entries.Select(static e => e.Id));
        Assert.Contains(presented.Nodes.OfType<NavItemNode>(), n => n.Key == "session-c-6c8edd77");
    }

    [Fact]
    public void ANodeCardIsDrawnInsideACardFrame()
    {
        const string nodeJson = """{"kind":"column","children":[{"kind":"text","value":"Apple"}]}""";
        var view = ViewOf(entries:
        [
            new Said(EntryIds.Said("r1"), Owner, At, "Show fruit"),
            new Returned(EntryIds.Result("r1", "c1"), Ino, At, "focus_show", """{"text":"Shown.","ok":true}""", true, new CardEntry("node", "Fruit", nodeJson)),
            new Said(EntryIds.Reply("r1"), Ino, At, "Here is the card."),
        ]);

        var conversation = InoDrawing.Conversation(new InoAppState(), view: view);
        var assistant = conversation.Messages[1];
        var card = Assert.IsType<Card>(Assert.Single(assistant.Cards!));
        Assert.Equal("Fruit", card.Title);
        var innerColumn = Assert.IsType<Column>(Assert.Single(card.Children));
        var innerText = Assert.IsType<Text>(Assert.Single(innerColumn.Children));
        Assert.Equal("Apple", innerText.Value);
    }

    [Fact]
    public void AnInvalidStoredNodeCardRendersNothing()
    {
        var nodes = InoView.RenderCardNodes(
            new InoCard("node-bad", "node", "Hostile", "", IFocus.DefaultKey, "done")
            {
                Node = """{"kind":"neuronRef","refKind":"slot","neuronUri":"x"}""",
            }).ToArray();

        Assert.Empty(nodes);
    }

    [Fact]
    public void ThreadPropsNameTheConversation()
    {
        var convId = "c-" + new string('a', 32);
        var state = new InoAppState { ConversationId = convId };
        Assert.Equal("conversations/" + convId, InoView.ThreadProps(state).Conversation);
        Assert.Equal("conversations/main", InoView.ThreadProps(new InoAppState()).Conversation);
    }

    private static IReadOnlyList<string> Texts(View view)
        => new PresentedView("main", view).Texts;

    private static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
