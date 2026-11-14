using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoViewFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnEmptyConversationHasAnEmptyState()
    {
        var c = InoView.Conversation(new InoAppState());
        Assert.Equal(("What do you need?", 0, true), (c.Empty!.Title, c.Messages.Count, c.Composer.Enabled));
    }

    [Fact]
    public void TheComposerIsOffWhileIAmWaitingForAReply()
        => Assert.False(InoView.Conversation(new InoAppState() with { ActiveRunId = "r1" }).Composer.Enabled);

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

        var c = InoView.Conversation(s);
        Assert.Equal(("user", "hello", "sending"), (c.Messages[^1].Role, c.Messages[^1].Text, c.Messages[^1].Status));
        Assert.Contains("hello", Texts(InoView.Screen(s)));
    }

    [Fact]
    public void AnErrorIsDrawnWithItsAction()
    {
        var c = InoView.Conversation(new InoAppState() with { Error = new InoError("no-provider"), Status = InoModel.Failed });
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

        var screen = InoView.Screen(s);
        var texts = Texts(screen);
        Assert.Contains("Ino has no model to answer with.", texts);
        Assert.Contains("Use local model (qwen2.5-coder:14b)", texts);

        var conversation = InoView.Conversation(s);
        var choiceCard = Assert.Single(conversation.Messages[^1].Cards!.OfType<ChoiceCardNode>());
        Assert.Contains(choiceCard.Options, o => o.Title == "Local Ollama · qwen2.5-coder:14b");
        var keyCard = Assert.Single(conversation.Messages[^1].Cards!.OfType<Card>());
        Assert.Contains(keyCard.Children, c => c is Field { Key: InoView.ProviderCredentialKey });
    }

    [Fact]
    public void StopIsOfferedOnlyWhileARunIsActive()
    {
        Assert.Null(InoView.Conversation(new InoAppState()).Stop);
        Assert.NotNull(InoView.Conversation(new InoAppState() with { ActiveRunId = "r1" }).Stop);
    }

    [Fact]
    public void OldTurnsAreNotDrawn()
    {
        var s = new InoAppState() with { Turns = [new InoTurn("old question", "old reply", "agent", 0)] };
        Assert.DoesNotContain("old reply", Texts(InoView.Screen(s)));
    }

    [Fact]
    public void AViewOf512LongMessagesStaysUnderTheLimit()
    {
        var many = Enumerable.Range(0, 512)
            .Select(i => new InoMessage($"m{i}", i % 2 == 0 ? "user" : "assistant", new string('x', 8_000)))
            .ToArray();
        var s = new InoAppState() with { Messages = many };
        Assert.True(InoView.Shown(s).Count <= InoView.MaxRenderedMessages);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(InoView.Screen(s), JsonSerializerOptions.Web).Length < 2 * 1024 * 1024);
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
            var texts = Texts(InoView.Screen(s));
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

        var neuron = new AspireNeuron(config);
        var resources = await neuron.GetResourcesAsync();
        Assert.Contains(resources, r => r.Name == "ollama" && r.Endpoint == "http://127.0.0.1:11434" && !r.Endpoint.Contains("pw123", StringComparison.Ordinal));

        var traces = await neuron.GetTracesAsync("IntoChat.OS.InoIntrospectionTest", errorsOnly: true);
        var captured = Assert.Single(traces, t => t.Name == "InoTurnProbe");
        Assert.Equal("Error", captured.Status);
        Assert.DoesNotContain("secretTokenValue12345", captured.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingApprovalCardsCarryAResolveActionWhileResolvedCardsDoNot()
    {
        var pending = Assert.IsType<ApprovalCardNode>(Assert.Single(InoView.RenderCardNodes(
            new InoCard("plan-42", "approval", "Open Draft PR", "Create draft PR on intochat/os", "f2pr", "pending", "owner"))));
        Assert.Equal(new Resolve("plan-42"), pending.Resolve);
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
        Assert.Equal("brand:github", inboxApproval.Icon);
        Assert.Equal("GitHub", inboxApproval.CapabilityLabel);
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

        var conversation = InoView.Conversation(state);
        Assert.Equal("Sonnet 4.6", conversation.Composer.ActiveModelLabel);
        Assert.Equal("provider:anthropic", conversation.Composer.ActiveProviderIcon);

        var assistant = conversation.Messages[1];
        Assert.Equal("Sonnet 4.6", assistant.Model);
        Assert.Equal("Sonnet 4.6", assistant.ModelLabel);
        Assert.Equal("provider:anthropic", assistant.ProviderIcon);
        Assert.NotNull(assistant.Activities);
        Assert.Equal(
            [
                "Aspire · Resources",
                "Aspire",
                "Ino Studio",
                "GitHub",
                "CodeGraph · Search Symbols",
            ],
            assistant.Activities!.Select(static a => a.Title).ToArray());
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
        Assert.Equal(("Aspire · Resources", "brand:aspire"), (aspireVisual.Label, aspireVisual.IconKey));

        var codeGraphVisual = InoView.ResolveActivityVisual(new InoActivity("a5", "ICodeGraph.SearchSymbols"));
        Assert.Equal(("CodeGraph · Search Symbols", "capability:codegraph"), (codeGraphVisual.Label, codeGraphVisual.IconKey));

        var modelVisual = InoView.ResolveModelVisual("claude-sonnet-4-6");
        Assert.NotNull(modelVisual);
        Assert.Equal(("Sonnet 4.6", "provider:anthropic"), (modelVisual.Value.Label, modelVisual.Value.IconKey));
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
        var conversation = InoView.Conversation(projected);
        var assistant = conversation.Messages[1];

        Assert.Equal("Qwen 2.5 Coder 14B", assistant.Model);
        Assert.Equal("Qwen 2.5 Coder 14B", assistant.ModelLabel);
        Assert.Equal("provider:ollama", assistant.ProviderIcon);
        Assert.Equal(
            ["Aspire · Resources", "PhotoCraft · Execute Command"],
            assistant.Activities!.Select(static a => a.Title).ToArray());
        Assert.Equal(
            ["brand:aspire", "artcraft:photocraft"],
            assistant.Activities!.Select(static a => a.Icon).ToArray());
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
