using System.Text.Json;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using ConversationEntry = IntoChat.OS.Modules.Flutter.Conversations.Entry;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

internal static class States
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Person Owner = new(new AccountId("local"));
    private static readonly Participant.Agent Ino = new("ino");

    private static ConversationView ViewOf(AgentRun? active = null, AgentRun[]? runs = null, params ConversationEntry[] entries)
        => new(ConversationId.Parse("main"), 1, [Owner, Ino], entries, active, runs ?? [], false, []);

    private static string CardResult(InoCard card)
        => JsonSerializer.Serialize(new InoToolResult("ok", card), JsonSerializerOptions.Web);

    public static IReadOnlyList<(InoAppState State, ConversationView? View)> All { get; } =
    [
        (new InoAppState(), null),
        (InoModel.Sent(new InoAppState(), "hello", "p1", At), null),
        (
            new InoAppState() with { Title = "hello", Status = InoModel.Working, ActiveRunId = "r1" },
            ViewOf(
                active: new AgentRun("r1", AgentRunStatus.Running, "hello", At, Reply: "Thinking through this..."),
                entries:
                [
                    new Said(EntryIds.Said("r1"), Owner, At, "hello"),
                    new Returned(EntryIds.Result("r1", "a1"), Ino, At, "IntoChat.OS.Modules.Apps.IStudio.ProposeChange", "ok", true, null),
                ])
        ),
        (
            new InoAppState() with { Title = "hello", Status = InoModel.Ready },
            ViewOf(
                entries:
                [
                    new Said(EntryIds.Said("r1"), Owner, At, "hello"),
                    new Said(EntryIds.Reply("r1"), Ino, At, "Hi Vlad.", new ModelId("qwen2.5-coder:14b")),
                ])
        ),
        (
            new InoAppState() with
            {
                Status = InoModel.Failed,
                Error = new InoError("no-provider"),
                DetectedLocalModel = "qwen2.5-coder:14b",
                DetectedLocalModels = ["qwen2.5-coder:14b", "qwen3.5:4b"],
            },
            ViewOf(
                runs: [new AgentRun("r1", AgentRunStatus.Failed, "hello", At) { Failure = AgentFailure.NoProvider }],
                entries: [new Said(EntryIds.Said("r1"), Owner, At, "hello")])
        ),
        (
            new InoAppState() with { Status = InoModel.Failed, Error = new InoError("provider-rejected-key") },
            ViewOf(
                runs: [new AgentRun("r1", AgentRunStatus.Failed, "hello", At) { Failure = AgentFailure.ProviderRejectedKey }],
                entries: [new Said(EntryIds.Said("r1"), Owner, At, "hello")])
        ),
        (
            new InoAppState() with { Status = InoModel.Failed, Error = new InoError("provider-unavailable") },
            ViewOf(
                runs: [new AgentRun("r1", AgentRunStatus.Failed, "hello", At) { Failure = AgentFailure.ProviderUnavailable }],
                entries: [new Said(EntryIds.Said("r1"), Owner, At, "hello")])
        ),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("timed-out") }, null),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("budget") }, null),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("too-long") }, null),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("interrupted") }, null),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("tool-failed") }, null),
        (new InoAppState() with { Status = InoModel.Failed, Error = new InoError("unknown") }, null),
        (InoModel.Pause(new InoAppState(), "An interrupt holds the agent."), null),
        (
            new InoAppState() with
            {
                Approvals =
                [
                    new Approval("plan-1", ApprovalKind.FlowStep, "change-flow-1", "Push branch and open PR?", "Touches 2 files in repo os.", At, At.AddDays(7), "trace-1"),
                ],
            },
            null
        ),
        (
            new InoAppState(),
            ViewOf(
                entries:
                [
                    new Said(EntryIds.Said("r1"), Owner, At, "Show open deals"),
                    new Returned(EntryIds.Result("r1", "t1"), Ino, At, "data_query",
                        CardResult(new InoCard("t1", "table", "Open Deals", "2 rows", "neuron://local/ino/data", "ready", Columns: ["Account", "Stage"], Rows: [["Acme", "Proposal"], ["Beta", "Contract"]])), true, null),
                    new Returned(EntryIds.Result("r1", "c1"), Ino, At, "data_query",
                        CardResult(new InoCard("c1", "chart", "Pipeline", "2 bars", "neuron://local/ino/data", "ready", ChartKind: "bar", ChartPoints: [new InoChartPoint("p1", "Acme", 120), new InoChartPoint("p2", "Beta", 90)])), true, null),
                    new Returned(EntryIds.Result("r1", "s1"), Ino, At, "os_settings_open",
                        CardResult(new InoCard("s1", "settings", "Settings · models", "Open settings", "neuron://local/apps/settings", "ready")), true, null),
                    new Said(EntryIds.Reply("r1"), Ino, At, "Here are your deals."),
                ])
        ),
    ];
}
