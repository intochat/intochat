using DigitalBrain.UI;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class ConversationThreadProjectionFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Human Owner = new(new AccountId("owner"));
    private static readonly Participant.Assistant Assistant = new("agent");

    private static ConversationView View(
        string id = "main",
        long revision = 1,
        AgentRun? active = null,
        AgentRun[]? runs = null,
        bool blocked = false,
        string? blockedReason = null,
        EntryId? replacing = null,
        params ConversationEntry[] entries)
        => new(ConversationId.Parse(id), revision, [Owner, Assistant], entries, active, runs ?? [], blocked, [])
        {
            BlockedReason = blockedReason,
            Replacing = replacing,
        };

    private static Said UserSaid(string runId, string text)
        => new(EntryIds.Said(runId), Owner, At, text);

    private static Said AssistantReply(string runId, string text, ModelId? model = null, EntryId? replaces = null)
        => new(EntryIds.Reply(runId), Assistant, At, text, model, replaces);

    private static AgentRun Run(
        string runId,
        AgentRunStatus status,
        string prompt = "hi",
        string? reply = null,
        string? error = null,
        AgentFailure failure = AgentFailure.Unknown,
        ResolvedModel? model = null,
        bool posted = false,
        AgentToolCall[]? toolCalls = null)
        => new(runId, status, prompt, At, Reply: reply, Error: error)
        {
            Failure = failure,
            Model = model,
            Posted = posted,
            ToolCalls = toolCalls ?? [],
        };

    // ── MergeView ───────────────────────────────────────────────────────────

    [Fact]
    public void MergeView_returns_incoming_when_previous_is_null_or_full_or_conversation_changes()
    {
        var first = View(id: "c1", revision: 1, entries: [UserSaid("r1", "hello")]);
        var second = View(id: "c1", revision: 2, entries: [AssistantReply("r1", "world")]);
        var other = View(id: "c2", revision: 1, entries: [UserSaid("r2", "other")]);

        Assert.Same(first, ConversationThreadProjection.MergeView(null, first));
        Assert.Same(second, ConversationThreadProjection.MergeView(first, second, full: true));
        Assert.Same(other, ConversationThreadProjection.MergeView(first, other));
    }

    [Fact]
    public void MergeView_preserves_entries_on_empty_delta_while_updating_revision_and_active_run()
    {
        var previous = View(revision: 2, entries: [UserSaid("r1", "hello")]);
        var streaming = Run("r1", AgentRunStatus.Running, reply: "hel");
        var delta = View(revision: 3, active: streaming, blocked: true, blockedReason: "paused by user");

        var merged = ConversationThreadProjection.MergeView(previous, delta);

        Assert.Equal(3, merged.Revision);
        Assert.Equal("hel", merged.Active?.Reply);
        Assert.True(merged.Blocked);
        Assert.Equal("paused by user", merged.BlockedReason);
        Assert.Single(merged.Entries);
        Assert.Equal(EntryIds.Said("r1"), merged.Entries[0].Id);
    }

    [Fact]
    public void MergeView_appends_new_entries_and_deduplicates_by_entry_id()
    {
        var e1 = UserSaid("r1", "hello");
        var e2 = AssistantReply("r1", "world");
        var e3 = UserSaid("r2", "next");

        var previous = View(revision: 1, entries: [e1]);
        var delta = View(revision: 2, entries: [e1, e2, e3]);

        var merged = ConversationThreadProjection.MergeView(previous, delta);

        Assert.Equal([e1.Id, e2.Id, e3.Id], merged.Entries.Select(e => e.Id));
    }

    // ── ToThreadMessages: Streaming, Turns, Superseded, Models ──────────────

    [Fact]
    public void ToThreadMessages_projects_streaming_text_deltas_and_hides_active_once_reply_is_posted()
    {
        var streamingView = View(
            active: Run("r1", AgentRunStatus.Running, reply: "part 1..."),
            entries: [UserSaid("r1", "tell me")]);

        var streaming = ConversationThreadProjection.ToThreadMessages(streamingView);
        Assert.Equal(2, streaming.Count);
        Assert.Equal(("user", "tell me", "completed"), (streaming[0].Role, streaming[0].Text, streaming[0].Status));
        Assert.Equal((EntryIds.Reply("r1").Value, "assistant", "part 1...", "running"),
            (streaming[1].Id, streaming[1].Role, streaming[1].Text, streaming[1].Status));
        Assert.False(streaming[1].CanRegenerate);

        var updatedDelta = View(
            revision: 2,
            active: Run("r1", AgentRunStatus.Running, reply: "part 1... part 2"),
            entries: []);
        var merged = ConversationThreadProjection.MergeView(streamingView, updatedDelta);
        Assert.Equal("part 1... part 2", ConversationThreadProjection.ToThreadMessages(merged)[^1].Text);

        var completedView = View(
            active: Run("r1", AgentRunStatus.Running, reply: "stale"),
            entries: [UserSaid("r1", "tell me"), AssistantReply("r1", "final answer")]);
        var completed = ConversationThreadProjection.ToThreadMessages(completedView);
        Assert.Equal(2, completed.Count);
        Assert.Equal(("final answer", "completed"), (completed[1].Text, completed[1].Status));
    }

    [Fact]
    public void ToThreadMessages_hides_superseded_replies_from_Replaces_and_Replacing()
    {
        var view = View(
            replacing: EntryIds.Reply("r2"),
            entries:
            [
                UserSaid("r1", "hi"),
                AssistantReply("r1", "first"),
                AssistantReply("r2", "second", replaces: EntryIds.Reply("r1")),
                AssistantReply("r3", "third"),
            ]);

        var messages = ConversationThreadProjection.ToThreadMessages(view);

        Assert.Equal(["hi", "third"], messages.Select(m => m.Text));
        Assert.Equal([EntryIds.Reply("r1"), EntryIds.Reply("r2")], ConversationThreadProjection.SupersededIds(view).OrderBy(e => e.Value));
    }

    [Fact]
    public void FormatModelChip_formats_known_unknown_and_device_model_references()
    {
        var known = KnownModels.All[0];
        var (knownLabel, knownIcon) = ConversationThreadProjection.FormatModelChip(known.Id.Value);
        Assert.Equal(known.Name, knownLabel);
        Assert.Equal(known.IconKey(), knownIcon);

        var (unknownLabel, unknownIcon) = ConversationThreadProjection.FormatModelChip("custom/my-model");
        Assert.Equal("custom/my-model", unknownLabel);
        Assert.Null(unknownIcon);

        var (deviceLabel, deviceIcon) = ConversationThreadProjection.FormatModelChip($"device:workstation/{known.Id.Value}");
        Assert.Equal($"{known.Name} · workstation", deviceLabel);
        Assert.Equal(known.IconKey(), deviceIcon);

        var (localDeviceLabel, localDeviceIcon) = ConversationThreadProjection.FormatModelChip("device:rig/ollama-qwen");
        Assert.Equal("ollama-qwen · rig", localDeviceLabel);
        Assert.Equal("provider:device", localDeviceIcon);

        Assert.Equal((null, null), ConversationThreadProjection.FormatModelChip("  "));
    }

    // ── ToThreadMessages: Error / Retry / Non-Completed States ──────────────

    [Fact]
    public void ToThreadMessages_projects_failed_cancelled_and_interrupted_runs_with_default_and_custom_failure_text()
    {
        var failed = Run("r1", AgentRunStatus.Failed, reply: "half", failure: AgentFailure.ProviderUnavailable);
        var cancelled = Run("r2", AgentRunStatus.Cancelled, reply: "stopped here");
        var interrupted = Run("r3", AgentRunStatus.Interrupted);
        var view = View(
            runs: [failed, cancelled, interrupted],
            entries: [UserSaid("r1", "q1"), UserSaid("r2", "q2"), UserSaid("r3", "q3")]);

        var defaults = ConversationThreadProjection.ToThreadMessages(view);
        Assert.Equal(6, defaults.Count);
        Assert.Equal(("failed", "half", "The model provider did not answer."), (defaults[1].Status, defaults[1].Text, defaults[1].Failure));
        Assert.Equal(("cancelled", "stopped here", "Stopped."), (defaults[3].Status, defaults[3].Text, defaults[3].Failure));
        Assert.Equal(("interrupted", "", "The run was interrupted."), (defaults[5].Status, defaults[5].Text, defaults[5].Failure));

        var custom = ConversationThreadProjection.ToThreadMessages(
            view,
            new ConversationThreadOptions { DescribeFailure = code => $"custom:{code}" });
        Assert.Equal("custom:provider-unavailable", custom[1].Failure);
        Assert.Equal("custom:cancelled", custom[3].Failure);
        Assert.Equal("custom:interrupted", custom[5].Failure);
    }

    [Fact]
    public void ToThreadMessages_replaces_trailing_failed_bubble_for_orphan_failed_retry_run()
    {
        var initialFailure = Run("r1", AgentRunStatus.Failed, failure: AgentFailure.ProviderUnavailable);
        var retryFailure = Run("r2", AgentRunStatus.Failed, failure: AgentFailure.ProviderRejectedKey);
        var view = View(
            runs: [initialFailure, retryFailure],
            entries: [UserSaid("r1", "try this")]);

        var messages = ConversationThreadProjection.ToThreadMessages(view);

        Assert.Equal(2, messages.Count);
        Assert.Equal(EntryIds.Reply("r2").Value, messages[1].Id);
        Assert.Equal("The model provider refused the key.", messages[1].Failure);
    }

    // ── ToThreadMessages: Tool Calls & Node Cards ───────────────────────────

    [Fact]
    public void ToThreadMessages_projects_tool_call_activities_and_parses_tool_error_payloads()
    {
        var called1 = new Called(EntryIds.Call("r1", "c1"), Assistant, At, "crm_lookup", """{"id":42}""");
        var ret1 = new Returned(EntryIds.Result("r1", "c1"), Assistant, At, "crm_lookup", """{"name":"Ada"}""", Ok: true, Card: null);
        var ret2 = new Returned(EntryIds.Result("r1", "c2"), Assistant, At, "crm_save", """{"error":"Permission denied"}""", Ok: false, Card: null);
        var view = View(entries: [UserSaid("r1", "check ada"), called1, ret1, ret2, AssistantReply("r1", "Done.")]);

        var reply = ConversationThreadProjection.ToThreadMessages(view)[^1];

        Assert.Equal(2, reply.Activities.Count);
        Assert.Equal(("c1", "crm_lookup", "done", """{"name":"Ada"}"""),
            (reply.Activities[0].Id, reply.Activities[0].Title, reply.Activities[0].Status, reply.Activities[0].Detail));
        Assert.Equal(("c2", "crm_save", "failed", "Permission denied"),
            (reply.Activities[1].Id, reply.Activities[1].Title, reply.Activities[1].Status, reply.Activities[1].Detail));
    }

    [Theory]
    [InlineData("""{"error":"Bad token"}""", "Bad token")]
    [InlineData("""{"text":"Not found"}""", "Not found")]
    [InlineData("""{"message":"Timeout"}""", "Timeout")]
    [InlineData("plain error text", "plain error text")]
    [InlineData("", "Tool failed.")]
    [InlineData(null, "Tool failed.")]
    public void ExtractErrorText_reads_known_json_fields_or_falls_back(string? payload, string expected)
        => Assert.Equal(expected, ConversationThreadProjection.ExtractErrorText(payload));

    [Fact]
    public void ToThreadMessages_renders_valid_node_cards_and_rejects_invalid_node_cards()
    {
        const string validNode = """{"kind":"column","children":[{"kind":"text","value":"KPI: 99%"}]}""";
        const string invalidNode = """{"kind":"not-a-safe-node"}""";

        var okCard = new Returned(
            EntryIds.Result("r1", "c1"), Assistant, At, "metrics", """{"ok":true}""", Ok: true,
            Card: new CardEntry("node", "Metrics", validNode));
        var badCard = new Returned(
            EntryIds.Result("r1", "c2"), Assistant, At, "metrics", """{"ok":true}""", Ok: true,
            Card: new CardEntry("node", "Unsafe", invalidNode));
        var view = View(entries: [UserSaid("r1", "show metrics"), okCard, badCard, AssistantReply("r1", "Shown.")]);

        var reply = ConversationThreadProjection.ToThreadMessages(view)[^1];

        var card = Assert.IsType<Card>(Assert.Single(reply.Cards));
        Assert.Equal("Metrics", card.Title);
        var col = Assert.IsType<Column>(Assert.Single(card.Children));
        Assert.Equal("KPI: 99%", Assert.IsType<Text>(Assert.Single(col.Children)).Value);
    }

    [Fact]
    public void ToThreadMessages_invokes_custom_hooks_in_deterministic_card_order()
    {
        var ret = new Returned(EntryIds.Result("r1", "c1"), Assistant, At, "custom_tool", """{"ok":true}""", Ok: true, Card: null);
        var view = View(entries: [UserSaid("r1", "go"), ret, AssistantReply("r1", "raw text")]);

        var options = new ConversationThreadOptions
        {
            TransformAssistantText = _ => ("clean text", [new Text("inline")]),
            FormatActivity = (r, _, callId) => new ActivityNode(callId, $"Hook:{r.Tool}", "done"),
            AppendStepCards = (_, _, cards) => cards.Add(new Text("step")),
            AppendRunCards = (_, _, _, cards) => cards.Add(new Text("run")),
            AppendSaidCards = (_, cards) => cards.Add(new Text("said")),
        };

        var reply = ConversationThreadProjection.ToThreadMessages(view, options)[^1];

        Assert.Equal("clean text", reply.Text);
        Assert.Equal("Hook:custom_tool", Assert.Single(reply.Activities).Title);
        Assert.Equal(["step", "run", "said", "inline"], reply.Cards.OfType<Text>().Select(t => t.Value));
    }

    [Fact]
    public void ToThreadMessages_marks_only_latest_completed_run_reply_regenerable_and_not_while_running()
    {
        var idleView = View(entries:
        [
            UserSaid("r1", "a"),
            AssistantReply("r1", "b"),
            UserSaid("r2", "c"),
            AssistantReply("r2", "d"),
        ]);
        Assert.Equal(
            [false, true],
            ConversationThreadProjection.ToThreadMessages(idleView)
                .Where(m => m.Role == "assistant")
                .Select(m => m.CanRegenerate));

        var runningView = View(
            active: Run("r2", AgentRunStatus.Running, reply: "typing"),
            entries: [UserSaid("r1", "a"), AssistantReply("r1", "b"), UserSaid("r2", "c")]);
        Assert.DoesNotContain(ConversationThreadProjection.ToThreadMessages(runningView), m => m.CanRegenerate);

        var postOnly = View(entries: [new Said(EntryIds.Post("welcome"), Assistant, At, "Hello!")]);
        Assert.DoesNotContain(ConversationThreadProjection.ToThreadMessages(postOnly), m => m.CanRegenerate);
    }

    // ── CapAndAppendPending ─────────────────────────────────────────────────

    [Fact]
    public void CapAndAppendPending_respects_message_and_char_budgets_cuts_oversized_newest_and_appends_pending()
    {
        var messages = new List<ThreadMessage>
        {
            new("m1", "user", "aaaa", "completed"),
            new("m2", "assistant", "bbbb", "completed") { CanRegenerate = true },
            new("m3", "user", "cccc", "completed"),
            new("m4", "assistant", "dddd", "completed") { CanRegenerate = true },
        };

        var capped = ConversationThreadProjection.CapAndAppendPending(
            messages,
            pendingId: "p1",
            pendingText: "pending prompt",
            maxMessages: 2,
            maxChars: 20);

        Assert.Equal(["m3", "m4", "pending/p1"], capped.Select(m => m.Id));
        Assert.False(capped[1].CanRegenerate);
        Assert.Equal(("user", "pending prompt", "sending"), (capped[2].Role, capped[2].Text, capped[2].Status));

        var oversized = ConversationThreadProjection.CapAndAppendPending(
            [new ThreadMessage("big", "assistant", new string('z', 50), "completed")],
            maxMessages: 5,
            maxChars: 10);
        Assert.Equal(10, Assert.Single(oversized).Text.Length);
    }

    // ── ToThreadProps ───────────────────────────────────────────────────────

    [Fact]
    public void ToThreadProps_derives_ready_working_paused_and_failed_states_from_view()
    {
        var empty = ConversationThreadProjection.ToThreadProps(
            View(id: "agent-main"),
            new ConversationThreadPropsOptions { EmptyText = "Ask anything" });
        Assert.Equal(("ready", true, false, "Ask anything", "conversations/agent-main"),
            (empty.Status, empty.CanSend, empty.Working, empty.EmptyText, empty.Conversation));
        Assert.Null(empty.Error);

        var working = ConversationThreadProjection.ToThreadProps(
            View(active: Run("r1", AgentRunStatus.Running, reply: "working"), entries: [UserSaid("r1", "go")]),
            new ConversationThreadPropsOptions { EmptyText = "Ask anything" });
        Assert.Equal(("working", false, true, null), (working.Status, working.CanSend, working.Working, working.EmptyText));

        var paused = ConversationThreadProjection.ToThreadProps(
            View(blocked: true, blockedReason: "Waiting on approval"));
        Assert.Equal(("paused", false), (paused.Status, paused.CanSend));
        Assert.Equal(("Conversation is paused.", "Waiting on approval"), (paused.Error?.What, paused.Error?.Detail));

        var failedRun = Run("r1", AgentRunStatus.Failed, failure: AgentFailure.ProviderUnavailable);
        var failed = ConversationThreadProjection.ToThreadProps(
            View(runs: [failedRun], entries: [UserSaid("r1", "go")]));
        Assert.Equal("failed", failed.Status);
        // Inline failed assistant bubble already shows the failure, so banner error is suppressed.
        Assert.Null(failed.Error);
    }

    [Fact]
    public void ToThreadProps_derives_model_chip_from_view_or_selected_model_and_suppresses_duplicate_retry_error()
    {
        var known = KnownModels.All[0];
        var withReplyModel = View(entries: [UserSaid("r1", "hi"), AssistantReply("r1", "yo", known.Id)]);

        var derived = ConversationThreadProjection.ToThreadProps(withReplyModel);
        Assert.Equal((known.Id.Value, known.Name, known.IconKey()),
            (derived.ActiveModelId, derived.ActiveModelLabel, derived.ActiveProviderIcon));

        var suppressed = ConversationThreadProjection.ToThreadProps(
            withReplyModel,
            new ConversationThreadPropsOptions
            {
                ShownMessages = [new ThreadMessage("r1/reply", "assistant", "", "failed") { Failure = "Oops" }],
                Error = new ChatThreadError("Oops", ActionLabel: "Try again", Retry: true),
            });
        Assert.Null(suppressed.Error);

        var threadFailureWins = ConversationThreadProjection.ToThreadProps(
            withReplyModel,
            new ConversationThreadPropsOptions
            {
                ThreadFailure = "Disconnected",
                Error = new ChatThreadError("Ignored", ActionLabel: "Try again", Retry: true),
            });
        Assert.Equal("Disconnected", threadFailureWins.Error?.What);
    }
}
