using System.ComponentModel;
using System.Text;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Memory;

[GenerateSerializer, Alias("ai.session-journal.status")]
public enum SessionBlockStatus
{
    Active = 0,
    Finalized = 1,
}

[GenerateSerializer, Alias("ai.session-journal.block")]
public sealed record SessionJournalBlock
{
    [Id(0)] public int SessionNumber { get; init; } = 1;
    [Id(1)] public string Date { get; init; } = "";
    [Id(2)] public SessionBlockStatus Status { get; init; }
    [Id(3)] public string? ConversationId { get; init; }
    [Id(4)] public string? ProjectBrief { get; init; }
    [Id(5)] public string Goal { get; init; } = "";
    [Id(6)] public string[] Deliverables { get; init; } = [];
    [Id(7)] public string[] Decisions { get; init; } = [];
    [Id(8)] public string[] OpenThreads { get; init; } = [];
    [Id(9)] public DateTimeOffset StartedAt { get; init; }
    [Id(10)] public DateTimeOffset UpdatedAt { get; init; }
    [Id(11)] public DateTimeOffset? EndedAt { get; init; }

    public string FormatMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("## Session ").Append(SessionNumber).AppendLine();
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(ProjectBrief))
        {
            sb.AppendLine("### Project");
            sb.AppendLine(ProjectBrief.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("### Goal");
        sb.AppendLine(string.IsNullOrWhiteSpace(Goal) ? "(pending)" : Goal.Trim());
        sb.AppendLine();
        AppendBullets(sb, "Deliverables", Deliverables);
        sb.AppendLine();
        AppendBullets(sb, "Decisions", Decisions);
        sb.AppendLine();
        AppendBullets(sb, "Open threads", OpenThreads);
        return sb.ToString().TrimEnd();
    }

    private static void AppendBullets(StringBuilder sb, string heading, string[] items)
    {
        sb.Append("### ").AppendLine(heading);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                sb.Append("- ").AppendLine(item.Trim());
            }
        }
    }
}

[GenerateSerializer, Alias("ai.session-journal.day")]
public sealed record SessionJournalDay
{
    [Id(0)] public string Date { get; init; } = "";
    [Id(1)] public SessionJournalBlock[] Sessions { get; init; } = [];

    public SessionJournalDay() { }

    public SessionJournalDay(string date, SessionJournalBlock[] sessions)
    {
        Date = date;
        Sessions = sessions ?? [];
    }

    public string FormatMarkdown()
        => string.Join("\n\n", Sessions.OrderBy(s => s.SessionNumber).Select(s => s.FormatMarkdown()));
}

[GenerateSerializer, Alias("ai.session-journal.milestone")]
public sealed record SessionMilestone
{
    [Id(0)] public string? ConversationId { get; init; }
    [Id(1)] public int? SessionNumber { get; init; }
    [Id(2)] public string? Date { get; init; }
    [Id(3)] public string? Goal { get; init; }
    [Id(4)] public string? ProjectBrief { get; init; }
    [Id(5)] public string[]? Deliverables { get; init; }
    [Id(6)] public string[]? Decisions { get; init; }
    [Id(7)] public string[]? OpenThreads { get; init; }
    [Id(8)] public string[]? ResolvedThreads { get; init; }
}

[GenerateSerializer, Alias("ai.session-journal.returning-view")]
public sealed record SessionReturningView
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public string? SourceDate { get; init; }
    [Id(2)] public bool IsPriorDay { get; init; }
    [Id(3)] public string? ActiveProjectBrief { get; init; }
    [Id(4)] public string[] OpenThreads { get; init; } = [];
    [Id(5)] public SessionJournalBlock[] Sessions { get; init; } = [];
    [Id(6)] public string FormattedMarkdown { get; init; } = "";
}

[DigitalBrain.Signal("ai.session-journal.updated")]
public sealed record SessionJournalUpdated(
    string JournalId,
    long Revision,
    string Date,
    int SessionNumber,
    SessionBlockStatus Status) : DigitalBrain.Signal;

[NeuronAlias(NeuronType), DefaultNeuronType(NeuronType)]
[Description("Daily YYYY-MM-DD numbered Session N journal tracking Goal, Deliverables, Decisions, OpenThreads, and optional Project brief pointers across agent and flow milestones.")]
public interface ISessionJournal
{
    const string NeuronType = "ai.session-journal";
    const string DefaultKey = "default";

    static NeuronId NeuronIdOf(BrainId brain, string key = DefaultKey) => new(brain, NeuronType, key);

    static string Key(BrainId brain, string key = DefaultKey) => NeuronIdOf(brain, key).FullKey();

    [Description("Open or resume a numbered Session N block in the daily YYYY-MM-DD log.")]
    Task<SessionJournalBlock> OpenSession(string? conversationId = null, string? goal = null, string? projectBrief = null, string? date = null);

    [Description("Record turn or workflow milestones (Goal, Deliverables, Decisions, OpenThreads, ResolvedThreads, ProjectBrief) into the active daily Session N block.")]
    Task<SessionJournalBlock> RecordMilestone(SessionMilestone milestone);

    [Description("Finalize an existing Session N block on wrap-up without creating a new block.")]
    Task<SessionJournalBlock> FinalizeSession(int? sessionNumber = null, string? date = null, string[]? openThreads = null);

    [Description("Subscribe this journal to TurnCompleted signals from an agent in the same brain.")]
    Task FollowAgent(string agentKey);

    [ReadOnlyCall, Interleaves]
    Task<SessionJournalDay?> ReadDay(string? date = null);

    [Description("Read returning-mode session context: today's YYYY-MM-DD log, or the latest prior day only when today has none, surfacing OpenThreads and any Project brief pointer.")]
    [ReadOnlyCall, Interleaves]
    Task<SessionReturningView> ReadReturningView(string? todayDate = null);

    [ReadOnlyCall, Interleaves]
    Task<string[]> ListDates();
}
