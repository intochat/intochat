using System.Text.Json;
using System.Xml.Linq;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Tests.Unit.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
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
            render: session => InoDrawing.Screen(session.ToAppState()).Root,
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
            render: s => InoDrawing.Screen(s).Root,
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
        var owner = new Participant.Human(new AccountId("local"));
        var ino = new Participant.Assistant("ino");
        var slotCard = new InoCard("slot-usage", "slot", "Usage Live", "Live usage stream", "neuron://local/apps/usage/view", "ready");
        var appletCard = new InoCard("app-scratch", "applet", "Live App: scratch", "Interactive scratchpad", "neuron://local/apps/scratch", "running");
        var viewData = new ConversationView(
            ConversationId.Parse("main"),
            1,
            [owner, ino],
            [
                new Said(EntryIds.Said("r1"), owner, At, "Build a live FX monitor and open calculator"),
                new Returned(EntryIds.Result("r1", "c1"), ino, At, "studio_make", JsonSerializer.Serialize(new InoToolResult("ok", slotCard), JsonSerializerOptions.Web), true, null),
                new Returned(EntryIds.Result("r1", "c2"), ino, At, "studio_make", JsonSerializer.Serialize(new InoToolResult("ok", appletCard), JsonSerializerOptions.Web), true, null),
                new Said(EntryIds.Reply("r1"), ino, At, "Here are your live surfaces:\n[slot:unit.fx.rates] and slot://apps/calculator/view", new ModelId("claude-sonnet-4-6")),
            ],
            null,
            [],
            false,
            []);
        var state = new InoAppState() { Status = InoModel.Ready };

        var view = InoDrawing.Screen(state, view: viewData);
        var conversation = Assert.IsType<Conversation>(Assert.Single(Assert.IsType<Column>(view.Root).Children));
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

        var rendered = InoDrawing.Screen(vaultState);
        var secretCard = FindFirst<SecretCardNode>(rendered.Root);
        Assert.NotNull(secretCard);
        Assert.Contains("neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN", secretCard!.SavedIn, StringComparison.Ordinal);
        Assert.Equal("os://window/secure", secretCard.Enter?.Intent?.Arg);
    }

    [Fact]
    public void EmptyWorkingNeedsApprovalAndErrorStatesRenderCleanKitChrome()
    {
        // 1. Empty state
        var emptyState = new InoAppState() with { SelectedModel = "claude-sonnet-4-6" };
        var emptyConv = InoDrawing.Conversation(emptyState);
        Assert.Equal("ready", emptyConv.Status);
        Assert.Equal("What do you need?", emptyConv.Empty?.Title);
        Assert.True(emptyConv.Composer.Enabled);
        Assert.Equal("Sonnet 4.6", emptyConv.Composer.ActiveModelLabel);

        // 2. Working state
        var workingState = InoModel.Started(InoModel.Sent(emptyState, "Analyze pipeline", "p1", At), "run-42", "claude-sonnet-4-6");
        var workingConv = InoDrawing.Conversation(workingState);
        Assert.Equal("working", workingConv.Status);
        Assert.False(workingConv.Composer.Enabled);
        Assert.NotNull(workingConv.Stop);
        Assert.Equal(ChatThreadProps.StopKey, workingConv.Stop!.Key);

        // 3. Needs-approval docked prompt state
        var approvalState = emptyState with
        {
            Approvals =
            [
                new Approval("plan-7", ApprovalKind.FlowStep, "change-pr-7", "Open pull request?", "2 files changed", At, At.AddHours(1), "trace-7"),
            ],
        };
        // The approval waits under the conversation thread, in Ino's own screen (its actions are Ino's, not the thread's).
        var dockedApproval = Assert.IsType<ApprovalCardNode>(FindFirst<ApprovalCardNode>(InoDrawing.Screen(approvalState).Root));
        Assert.Null(dockedApproval.Resolve);

        // 4. Error + inline Kit SecretField setup state
        var owner = new Participant.Human(new AccountId("local"));
        var ino = new Participant.Assistant("ino");
        var errorView = new ConversationView(
            ConversationId.Parse("main"),
            1,
            [owner, ino],
            [new Said(EntryIds.Said("r1"), owner, At, "Explain architecture")],
            null,
            [new AgentRun("r1", AgentRunStatus.Failed, "Explain architecture", At) { Failure = AgentFailure.NoProvider }],
            false,
            []);
        var errorState = emptyState with
        {
            Status = InoModel.Failed,
            Error = new InoError("no-provider"),
            DetectedLocalModel = "qwen2.5-coder:14b",
        };
        var errorConv = InoDrawing.Conversation(errorState, view: errorView);
        Assert.Equal("failed", errorConv.Status);
        Assert.Equal("Ino has no model to answer with.", errorConv.Error?.What);
        Assert.Equal(ChatThreadProps.RetryKeyOf(EntryIds.Reply("r1").Value), errorConv.Messages[^1].Retry?.Key);
        Assert.Equal("Ino has no model to answer with.", errorConv.Messages[^1].Error?.What);

        // Model setup is Ino's own (its actions reach Ino): it is drawn under the conversation thread.
        var setupCard = Assert.IsType<Card>(FindFirst<Card>(InoDrawing.Screen(errorState, view: errorView).Root));
        var useLocalBtn = Assert.Single(setupCard.Children.OfType<Button>());
        Assert.Equal(InoView.UseLocalKey, useLocalBtn.Key);
        var secretField = Assert.Single(setupCard.Children.OfType<Field>());
        Assert.Equal(InoView.ProviderCredentialKey, secretField.Key);
        Assert.Equal(FieldKind.Secret, secretField.Kind);
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
