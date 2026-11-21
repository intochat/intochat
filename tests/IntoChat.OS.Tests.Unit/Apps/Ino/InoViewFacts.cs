using DigitalBrain;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoViewFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

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
        var s = InoModel.Sent(
            new InoAppState() with
            {
                Messages = [new("r0/user", "user", "earlier"), new("r0/assistant", "assistant", "ok")],
            },
            "hello",
            "p1",
            At);

        var c = InoDrawing.Conversation(s);
        Assert.Equal(("user", "hello", "sending"), (c.Messages[^1].Role, c.Messages[^1].Text, c.Messages[^1].Status));
        Assert.Contains("hello", Texts(InoDrawing.Screen(s)));
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
        var s = new InoAppState() with
        {
            Error = new InoError("no-provider"),
            Status = InoModel.Failed,
            DetectedLocalModel = "qwen2.5-coder:14b",
            DetectedLocalModels = ["qwen2.5-coder:14b", "qwen3.5:4b"],
            Messages =
            [
                new InoMessage("r1/user", "user", "Write a migration"),
                new InoMessage("r1/assistant", "assistant", "", "failed") { Failure = "no-provider" },
            ],
        };

        var screen = InoDrawing.Screen(s);
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
            .Select(i => new InoMessage($"m{i}", i % 2 == 0 ? "user" : "assistant", new string('x', 8_000)))
            .ToArray();
        var s = new InoAppState() with { Messages = many };
        Assert.True(InoView.Shown(s).Count <= InoView.MaxRenderedMessages);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(InoDrawing.Screen(s), JsonSerializerOptions.Web).Length < 2 * 1024 * 1024);
        Assert.Equal("m511", InoView.Shown(s)[^1].Id);
    }

    [Fact]
    public void OneEnormousReplyIsCutNotDropped()
    {
        var s = new InoAppState() with { Messages = [new("m0", "assistant", new string('x', 3_000_000))] };
        Assert.Equal(InoView.MaxRenderedChars, InoView.Shown(s).Single().Text.Length);
    }

    [Theory]
    [InlineData("neuron://")]
    [InlineData("IntoChat.")]
    [InlineData("app.ino")]
    [InlineData("ino.send")]
    [InlineData("InoApp")]
    public void NoInternalVocabularyReachesThePerson(string forbidden)
    {
        foreach (var s in States.All)
        {
            var texts = Texts(InoDrawing.Screen(s));
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
        var neuron = os.Get<IAspire>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("owner"), "aspire", "default"));
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
            Messages =
            [
                new InoMessage("r1/user", "user", "Add Multiply"),
                new InoMessage("r1/assistant", "assistant", "The change waits for your approval.")
                {
                    Cards =
                    [
                        new InoCard(
                            "plan-42",
                            "approval",
                            "Waiting for your approval: Add Multiply (0123abcd)",
                            "1 file(s) on ino/add-multiply (+1 / -0)",
                            "neuron://local/runtime.confirmations/confirmations",
                            "pending",
                            "neuron://local/apps/tasks#change-flow-42"),
                    ],
                },
            ],
        };

        var screen = InoDrawing.Screen(state);
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
        Assert.Null(InoApp.Bundle.Manifest.Toolbar);
    }

    [Fact]
    public void ConversationFormatsActivityTitlesAndModelBadgesUsingVisualCatalog()
    {
        var state = new InoAppState() with
        {
            SelectedModel = "claude-sonnet-4-6",
            Messages =
            [
                new InoMessage("r1/user", "user", "Inspect cluster and symbols"),
                new InoMessage("r1/assistant", "assistant", "All green.")
                {
                    Model = "claude-sonnet-4-6",
                    Activities =
                    [
                        new InoActivity("a1", "IAspire.GetResourcesAsync", "3 healthy"),
                        new InoActivity("a2", "aspire"),
                        new InoActivity("a3", "ino.studio"),
                        new InoActivity("a4", "IWorkTree"),
                        new InoActivity("a5", "ICodeGraph.SearchSymbols", "4 matches"),
                    ],
                },
            ],
        };

        var conversation = InoDrawing.Conversation(state);
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
    public void ProjectedChatFormatsNeuronsCallJsonIntoBrandActivityLabels()
    {
        var chat = new IntoChat.OS.Modules.Flutter.Chat.ChatState
        {
            Name = "local/ino/chats/main",
            Revision = 1,
            Selection = new IntoChat.OS.Modules.Flutter.Chat.ChatSelection("local/ino/conversations/main", 1),
            Projection = new IntoChat.OS.Modules.Flutter.Chat.ChatProjection(
                "local/ino/conversations/main",
                1,
                1,
                [
                    new IntoChat.OS.Modules.Flutter.Chat.ChatEntry("r1/user", IntoChat.OS.Modules.Flutter.Chat.ChatRole.User, "check health"),
                    new IntoChat.OS.Modules.Flutter.Chat.ChatEntry("r1/assistant", IntoChat.OS.Modules.Flutter.Chat.ChatRole.Assistant, "Healthy")
                    {
                        Model = "qwen2.5-coder:14b",
                        Activities =
                        [
                            new IntoChat.OS.Modules.Flutter.Chat.ChatActivity(
                                "call-1",
                                "neurons_call",
                                """{"contract":"IntoChat.OS.Modules.Microsoft.Aspire.IAspire","key":"local/aspire","method":"GetResourcesAsync"}""",
                                "ok"),
                            new IntoChat.OS.Modules.Flutter.Chat.ChatActivity(
                                "call-2",
                                "neurons_call",
                                """{"contract":"IntoChat.OS.Modules.ArtCraft.IArtCraftStudio","key":"local/artcraft/photocraft","method":"ExecuteCommand"}""",
                                "done"),
                        ],
                    },
                ]),
            Run = new IntoChat.OS.Modules.Flutter.Chat.ChatRunSnapshot(
                "local/ino/conversations/main",
                1,
                1,
                "r1",
                IntoChat.OS.Modules.Flutter.Chat.ChatRunStatus.Completed),
        };

        var projected = InoModel.Projected(new InoAppState(), chat, "local/ino/conversations/main");
        var conversation = InoDrawing.Conversation(projected);
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
    public void ANodeCardIsDrawnInsideACardFrame()
    {
        const string nodeJson = """{"kind":"column","children":[{"kind":"text","value":"Apple"}]}""";
        var state = new InoAppState() with
        {
            Messages =
            [
                new InoMessage("r1/user", "user", "Show fruit"),
                new InoMessage("r1/assistant", "assistant", "Here is the card.")
                {
                    Cards =
                    [
                        new InoCard("node-1", "node", "Fruit", "", IFocus.DefaultKey, "done") { Node = nodeJson },
                    ],
                },
            ],
        };

        var conversation = InoDrawing.Conversation(state);
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
