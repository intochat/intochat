using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

internal static class States
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<InoAppState> All { get; } =
    [
        new InoAppState(),
        InoModel.Sent(new InoAppState(), "hello", "p1", At),
        new InoAppState() with
        {
            Title = "hello",
            Status = InoModel.Working,
            ActiveRunId = "r1",
            Messages =
            [
                new InoMessage("r1/user", "user", "hello"),
                new InoMessage("r1/assistant", "assistant", "Thinking through this...", "running")
                {
                    Activities = [new InoActivity("a1", "IntoChat.OS.Modules.Apps.IInoStudio.ProposeChange", "ok")],
                },
            ],
        },
        new InoAppState() with
        {
            Title = "hello",
            Status = InoModel.Ready,
            Messages =
            [
                new InoMessage("r1/user", "user", "hello"),
                new InoMessage("r1/assistant", "assistant", "Hi Vlad.", "completed") { Model = "qwen2.5-coder:14b" },
            ],
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("no-provider"),
            DetectedLocalModel = "qwen2.5-coder:14b",
            DetectedLocalModels = ["qwen2.5-coder:14b", "qwen3.5:4b"],
            Messages = [new InoMessage("r1/user", "user", "hello"), new InoMessage("r1/assistant", "assistant", "", "failed") { Failure = "no-provider" }],
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("provider-rejected-key"),
            Messages = [new InoMessage("r1/user", "user", "hello"), new InoMessage("r1/assistant", "assistant", "", "failed") { Failure = "provider-rejected-key" }],
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("provider-unavailable"),
            Messages = [new InoMessage("r1/user", "user", "hello"), new InoMessage("r1/assistant", "assistant", "", "failed") { Failure = "provider-unavailable" }],
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("timed-out"),
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("budget"),
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("too-long"),
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("interrupted"),
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("tool-failed"),
        },
        new InoAppState() with
        {
            Status = InoModel.Failed,
            Error = new InoError("unknown"),
        },
        InoModel.Pause(new InoAppState(), "An interrupt holds the agent."),
        new InoAppState() with
        {
            Approvals =
            [
                new Approval("plan-1", ApprovalKind.FlowStep, "change-flow-1", "Push branch and open PR?", "Touches 2 files in repo os.", At, At.AddDays(7), "trace-1"),
            ],
        },
        new InoAppState() with
        {
            Messages =
            [
                new InoMessage("r1/user", "user", "Show open deals"),
                new InoMessage("r1/assistant", "assistant", "Here are your deals.")
                {
                    Cards =
                    [
                        new InoCard("t1", "table", "Open Deals", "2 rows", "neuron://local/ino/data", "ready", Columns: ["Account", "Stage"], Rows: [["Acme", "Proposal"], ["Beta", "Contract"]]),
                        new InoCard("c1", "chart", "Pipeline", "2 bars", "neuron://local/ino/data", "ready", ChartKind: "bar", ChartPoints: [new InoChartPoint("p1", "Acme", 120), new InoChartPoint("p2", "Beta", 90)]),
                        new InoCard("s1", "settings", "Settings · models", "Open settings", "neuron://local/apps/settings", "ready"),
                    ],
                },
            ],
        },
    ];
}
