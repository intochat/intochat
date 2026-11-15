using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class InoReactorViewFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplorerUnfoldsInoAggregateVerifiesInvariantsRuleRejectionsAndViewKeyUniqueness()
    {
        var choices = new ChoiceSet()
            .ForParameter("prompt", "Summarize Q4 renewals", "")
            .ForParameter("runId", "run-1")
            .ForParameter("reply", "All 3 renewals are healthy.")
            .ForParameter("code", "no-provider")
            .ForParameter("reason", "Approval required")
            .ForParameter("model", "qwen2.5-coder:14b");

        var graph = AggregateExplorer<InoSession>.Explore(
            render: session => InoView.Screen(session.ToAppState()).Root,
            choices: choices,
            maxStates: 48);

        graph.EnsureHealthy();
        Assert.True(graph.IsHealthy);
        Assert.Empty(graph.Violations);
        Assert.True(graph.States.Count >= 6, $"Expected at least 6 reachable InoSession states, got {graph.States.Count}.");

        // Accepted pure command transitions cover idle -> sending -> running -> completed / failed / paused / resumed
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.StartsWith("Send(\"Summarize Q4 renewals\"", StringComparison.Ordinal));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "StartRun(\"run-1\")");
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.StartsWith("CompleteTurn(", StringComparison.Ordinal));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "FailTurn(\"no-provider\")");
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Pause(\"Approval required\")");
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Resume()");
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Retry()");

        // Rejected self-loop transitions enforce named DDD rules
        Assert.Contains(graph.RejectedTransitions, t => t.FromId == t.ToId && t.BrokenRules.Contains("non_empty_prompt"));
        Assert.Contains(graph.RejectedTransitions, t => t.FromId == t.ToId && t.BrokenRules.Contains("not_busy"));
        Assert.Contains(graph.RejectedTransitions, t => t.FromId == t.ToId && t.BrokenRules.Contains("not_blocked"));
        Assert.Contains(graph.RejectedTransitions, t => t.FromId == t.ToId && t.BrokenRules.Contains("can_retry"));

        // Also explore InoAppState directly and verify StateGraphView + DOT export
        var appStateGraph = AggregateExplorer<InoAppState>.Explore(
            render: s => InoView.Screen(s).Root,
            maxStates: 8);
        appStateGraph.EnsureHealthy();

        var scene = StateGraphView(graph);
        Assert.Equal(graph.States.Count, scene.Nodes.Count);
        Assert.NotEmpty(scene.Synapses);

        var dot = graph.ToDot();
        Assert.Contains("digraph InoSession", dot, StringComparison.Ordinal);
        Assert.Contains("not_busy", dot, StringComparison.Ordinal);

        // Verify InoModel.DecideSend & DecideRetryPrompt rule enforcement on InoAppState
        var emptySend = InoModel.DecideSend(new InoAppState(), "   ", "p1", At);
        Assert.True(emptySend.IsRefused);
        Assert.Contains("non_empty_prompt", emptySend.Rejection.Rules);

        var busyState = InoModel.Sent(new InoAppState(), "first", "p1", At);
        var busySend = InoModel.DecideSend(busyState, "second", "p2", At);
        Assert.True(busySend.IsRefused);
        Assert.Contains("not_busy", busySend.Rejection.Rules);

        var overLimit = InoModel.DecideSend(new InoAppState(), new string('x', InoModel.MaxMessageChars + 1), "p3", At);
        Assert.True(overLimit.IsAccepted);
        Assert.Equal("too-long", overLimit.Next.Error?.Code);

        var emptyRetry = InoModel.DecideRetryPrompt(new InoAppState());
        Assert.True(emptyRetry.IsRefused);
        Assert.Contains("has_prior_prompt", emptyRetry.Rejection.Rules);
    }

    [Fact]
    public void AssistantTurnsRenderLiveInlineSlotsFromBracketTagsSlotUrisAndStructuredAppletCards()
    {
        var state = new InoAppState() with
        {
            Status = InoModel.Ready,
            Messages =
            [
                new InoMessage("r1/user", "user", "Build a live FX monitor and open calculator"),
                new InoMessage(
                    "r1/assistant",
                    "assistant",
                    "Here are your live surfaces:\n[slot:unit.fx.rates] and slot://apps/calculator/view")
                {
                    Model = "claude-sonnet-4-6",
                    Cards =
                    [
                        new InoCard("slot-usage", "slot", "Usage Live", "Live usage stream", "neuron://local/apps/usage/view", "ready"),
                        new InoCard("app-scratch", "applet", "Live App: scratch", "Interactive scratchpad", "neuron://local/apps/scratch", "running"),
                    ],
                },
            ],
        };

        var view = InoView.Screen(state);
        var conversation = Assert.IsType<Conversation>(view.Root);
        var assistant = conversation.Messages[1];

        // Inline slot markers are extracted cleanly out of prose and mounted as live NeuronRef Slot nodes
        Assert.DoesNotContain("[slot:", assistant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slot://", assistant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(assistant.Cards);

        var allSlots = CollectNeuronRefs(assistant.Cards!).ToArray();
        Assert.Contains(allSlots, s => s is { Kind: "unit", NeuronUri: "unit.fx.rates" });
        Assert.Contains(allSlots, s => s is { Kind: "view", NeuronUri: "apps/calculator/view" });
        Assert.Contains(allSlots, s => s is { Kind: "view", NeuronUri: "apps/usage/view" });
        Assert.Contains(allSlots, s => s is { Kind: "view", NeuronUri: "apps/scratch/view" });

        // Zero neuron:// URI leaks in rendered text or slot references
        var texts = new PresentedView("main", view).Texts;
        Assert.All(texts, t => Assert.DoesNotContain("neuron://", t, StringComparison.OrdinalIgnoreCase));
        Assert.All(allSlots, s => Assert.DoesNotContain("neuron://", s.NeuronUri, StringComparison.OrdinalIgnoreCase));

        // Wire JSON carries kind:"neuronRef" for Flutter ViewHost mounting
        var json = JsonSerializer.Serialize(view, RuntimeProtocol.OptionsFor(typeof(View)));
        Assert.Contains("\"kind\":\"neuronRef\"", json, StringComparison.Ordinal);
        Assert.Contains("\"neuronUri\":\"unit.fx.rates\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InoSourcesContainZeroInvoiceCheckLiteralsWhileVaultSecretCardsPreserveMetadataDataDriven()
    {
        var inoDir = LocateInoSourceDirectory();
        var files = Directory.GetFiles(inoDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var forbiddenScenarioLiteral = string.Concat("invoice", "-", "check");
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(forbiddenScenarioLiteral, content, StringComparison.OrdinalIgnoreCase);
        }

        // Data-driven verification: any state with an unprojected vault secret card preserves its vault URI without checking ConversationId
        var vaultState = new InoAppState(
            ConversationId: "c-arbitrary-conversation-id",
            Title: "Ledger Sync",
            Status: "Waiting on Secure Vault credential",
            VoiceStatus: "idle",
            Turns: [],
            Cards:
            [
                new InoCard(
                    "secret-1",
                    "secret",
                    "Tripletex Session Token",
                    "Sealed in Secure Vault",
                    "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN",
                    "pending"),
            ]);

        var rendered = InoView.Screen(vaultState);
        var secretCard = FindFirst<SecretCardNode>(rendered.Root);
        Assert.NotNull(secretCard);
        Assert.Contains("neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN", secretCard!.SavedIn, StringComparison.Ordinal);
        Assert.Equal("os://window/secure", secretCard.Enter?.Intent?.Arg);
    }

    [Fact]
    public void EmptyWorkingNeedsApprovalAndErrorStatesRenderCleanKitChromeAndStatusPills()
    {
        // 1. Empty state
        var emptyState = new InoAppState() with { SelectedModel = "claude-sonnet-4-6" };
        var emptyConv = InoView.Conversation(emptyState);
        Assert.Equal("ready", emptyConv.Status);
        Assert.Equal("What do you need?", emptyConv.Empty?.Title);
        Assert.True(emptyConv.Composer.Enabled);
        Assert.Equal("Sonnet 4.6", emptyConv.Composer.ActiveModelLabel);
        var emptySidePill = FindFirst<TagPill>(InoView.Side(emptyState).Root);
        Assert.NotNull(emptySidePill);
        Assert.Equal(("done", "check", "Ready"), (emptySidePill!.Tone, emptySidePill.Icon, emptySidePill.Label));

        // 2. Working state
        var workingState = InoModel.Started(InoModel.Sent(emptyState, "Analyze pipeline", "p1", At), "run-42", "claude-sonnet-4-6");
        var workingConv = InoView.Conversation(workingState);
        Assert.Equal("working", workingConv.Status);
        Assert.False(workingConv.Composer.Enabled);
        Assert.NotNull(workingConv.Stop);
        Assert.Equal(InoView.StopKey, workingConv.Stop!.Key);
        var workingSidePill = FindFirst<TagPill>(InoView.Side(workingState).Root);
        Assert.NotNull(workingSidePill);
        Assert.Equal(("working", "loader-circle", "Working"), (workingSidePill!.Tone, workingSidePill.Icon, workingSidePill.Label));

        // 3. Needs-approval docked prompt state
        var approvalState = emptyState with
        {
            Approvals =
            [
                new Approval("plan-7", ApprovalKind.FlowStep, "change-pr-7", "Open pull request?", "2 files changed", At, At.AddHours(1), "trace-7"),
            ],
        };
        var approvalConv = InoView.Conversation(approvalState);
        var dockedApproval = Assert.IsType<ApprovalCardNode>(approvalConv.Waiting);
        Assert.Null(dockedApproval.Resolve);
        var approvalSidePill = FindFirst<TagPill>(InoView.Side(approvalState).Root);
        Assert.NotNull(approvalSidePill);
        Assert.Equal(("accent", "hand", "Needs you"), (approvalSidePill!.Tone, approvalSidePill.Icon, approvalSidePill.Label));

        // 4. Error + inline Kit SecretField setup state
        var errorState = emptyState with
        {
            Status = InoModel.Failed,
            Error = new InoError("no-provider"),
            DetectedLocalModel = "qwen2.5-coder:14b",
            Messages =
            [
                new InoMessage("r1/user", "user", "Explain architecture"),
                new InoMessage("r1/assistant", "assistant", string.Empty, "failed") { Failure = "no-provider" },
            ],
        };
        var errorConv = InoView.Conversation(errorState);
        Assert.Equal("failed", errorConv.Status);
        Assert.Equal("Ino has no model to answer with.", errorConv.Error?.What);
        Assert.Equal(InoView.RetryKey, errorConv.Messages[^1].Retry?.Key);

        var setupCard = Assert.Single(errorConv.Messages[^1].Cards!.OfType<Card>());
        var useLocalBtn = Assert.Single(setupCard.Children.OfType<Button>());
        Assert.Equal(InoView.UseLocalKey, useLocalBtn.Key);
        var secretField = Assert.Single(setupCard.Children.OfType<Field>());
        Assert.Equal(InoView.ProviderCredentialKey, secretField.Key);
        Assert.Equal(FieldKind.Secret, secretField.Kind);

        var errorSidePill = FindFirst<TagPill>(InoView.Side(errorState).Root);
        Assert.NotNull(errorSidePill);
        Assert.Equal(("error", "triangle-alert", "Attention"), (errorSidePill!.Tone, errorSidePill.Icon, errorSidePill.Label));
    }

    private static IEnumerable<NeuronRef> CollectNeuronRefs(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is NeuronRef nr)
            {
                yield return nr;
            }

            foreach (var child in ChildrenOf(node))
            {
                foreach (var nested in CollectNeuronRefs([child]))
                {
                    yield return nested;
                }
            }
        }
    }

    private static T? FindFirst<T>(Node? root) where T : Node
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        foreach (var child in ChildrenOf(root))
        {
            if (FindFirst<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<Node> ChildrenOf(Node node) => node switch
    {
        Column col => col.Children,
        Row row => row.Children,
        Surface surface => surface.Children,
        Card card => card.Children,
        Grid grid => grid.Children,
        SplitPane split => [split.Leading, split.Trailing],
        _ => [],
    };

    private static string LocateInoSourceDirectory()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(InoApp).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var slnx = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var relProjectPath = slnx.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .First(p => p is not null && p.EndsWith("IntoChat.OS.Apps.csproj", StringComparison.OrdinalIgnoreCase))!;
        var appsDir = Path.GetDirectoryName(Path.Combine(root, relProjectPath.Replace('/', Path.DirectorySeparatorChar)))!;
        return Path.Combine(appsDir, "Ino");
    }
}
