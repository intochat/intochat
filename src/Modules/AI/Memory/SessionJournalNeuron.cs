using System.Globalization;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.State;
using IntoChat.OS.Modules.AI.Agents.Signals;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Modules.AI.Memory;

[GenerateSerializer, Alias("ai.session-journal.state")]
[ExportableState]
internal sealed record SessionJournalState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public SessionJournalDay[] Days { get; init; } = [];
}

[Neuron(ISessionJournal.NeuronType, State = "session-journal")]
internal sealed partial class SessionJournalNeuron(ISecretRedactor? redactor = null)
    : Neuron<SessionJournalState>, ISessionJournal
{
    private const int MaxKeptDays = 90;
    private const int MaxItemsPerSection = 64;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDatePattern();

    public async Task<SessionJournalBlock> OpenSession(
        string? conversationId = null,
        string? goal = null,
        string? projectBrief = null,
        string? date = null)
    {
        var now = Context.Time.GetUtcNow();
        var targetDate = await ResolveDateAsync("OpenSession", date, now).ConfigureAwait(true);
        var trimmedConv = NormalizeOptional(conversationId);
        var maskedGoal = goal is null ? null : CollapseLine(await MaskAsync(goal).ConfigureAwait(true));
        var trimmedBrief = NormalizeOptional(projectBrief);

        var day = FindDay(State, targetDate) ?? new SessionJournalDay(targetDate, []);
        var existing = trimmedConv is not null
            ? day.Sessions.LastOrDefault(s => s.Status == SessionBlockStatus.Active && string.Equals(s.ConversationId, trimmedConv, StringComparison.Ordinal))
            : null;

        SessionJournalBlock block;
        SessionJournalBlock[] updatedSessions;
        if (existing is not null)
        {
            block = existing with
            {
                Goal = !string.IsNullOrWhiteSpace(maskedGoal) && string.IsNullOrWhiteSpace(existing.Goal) ? maskedGoal : existing.Goal,
                ProjectBrief = trimmedBrief ?? existing.ProjectBrief,
                UpdatedAt = now,
            };
            updatedSessions = ReplaceSession(day.Sessions, block);
        }
        else
        {
            var nextNumber = day.Sessions.Length == 0 ? 1 : day.Sessions.Max(s => s.SessionNumber) + 1;
            block = new SessionJournalBlock
            {
                SessionNumber = nextNumber,
                Date = targetDate,
                Status = SessionBlockStatus.Active,
                ConversationId = trimmedConv,
                ProjectBrief = trimmedBrief,
                Goal = maskedGoal ?? "",
                StartedAt = now,
                UpdatedAt = now,
            };
            updatedSessions = [.. day.Sessions, block];
        }

        await SaveDayAsync(new SessionJournalDay(targetDate, updatedSessions), block).ConfigureAwait(true);
        return block;
    }

    public async Task<SessionJournalBlock> RecordMilestone(SessionMilestone milestone)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        var now = Context.Time.GetUtcNow();
        var targetDate = await ResolveDateAsync("RecordMilestone", milestone.Date, now).ConfigureAwait(true);
        var trimmedConv = NormalizeOptional(milestone.ConversationId);
        var day = FindDay(State, targetDate) ?? new SessionJournalDay(targetDate, []);

        var current = ResolveTargetBlock(day, milestone.SessionNumber, trimmedConv);
        if (current is null)
        {
            var nextNumber = day.Sessions.Length == 0 ? 1 : day.Sessions.Max(s => s.SessionNumber) + 1;
            current = new SessionJournalBlock
            {
                SessionNumber = nextNumber,
                Date = targetDate,
                Status = SessionBlockStatus.Active,
                ConversationId = trimmedConv,
                StartedAt = now,
                UpdatedAt = now,
            };
        }

        var goal = string.IsNullOrWhiteSpace(current.Goal) && !string.IsNullOrWhiteSpace(milestone.Goal)
            ? CollapseLine(await MaskAsync(milestone.Goal).ConfigureAwait(true))
            : current.Goal;
        var projectBrief = NormalizeOptional(milestone.ProjectBrief) ?? current.ProjectBrief;
        var deliverables = MergeItems(current.Deliverables, await MaskItemsAsync(milestone.Deliverables).ConfigureAwait(true));
        var decisions = MergeItems(current.Decisions, await MaskItemsAsync(milestone.Decisions).ConfigureAwait(true));
        var openThreads = MergeItems(current.OpenThreads, await MaskItemsAsync(milestone.OpenThreads).ConfigureAwait(true));

        if (milestone.ResolvedThreads is { Length: > 0 } resolved)
        {
            openThreads = openThreads
                .Where(open => !resolved.Any(r => !string.IsNullOrWhiteSpace(r) && open.Contains(r.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        var updatedBlock = current with
        {
            ConversationId = trimmedConv ?? current.ConversationId,
            Goal = goal,
            ProjectBrief = projectBrief,
            Deliverables = deliverables,
            Decisions = decisions,
            OpenThreads = openThreads,
            UpdatedAt = now,
        };

        var updatedSessions = ReplaceOrAppendSession(day.Sessions, updatedBlock);
        await SaveDayAsync(new SessionJournalDay(targetDate, updatedSessions), updatedBlock).ConfigureAwait(true);
        return updatedBlock;
    }

    public async Task<SessionJournalBlock> FinalizeSession(int? sessionNumber = null, string? date = null, string[]? openThreads = null)
    {
        var now = Context.Time.GetUtcNow();
        var targetDate = await ResolveDateAsync("FinalizeSession", date, now).ConfigureAwait(true);
        var day = FindDay(State, targetDate);
        await Check(
            "FinalizeSession",
            IntoChat.OS.UI.Decision.Rule("session_day_exists", day, static d => d is null || d.Sessions.Length == 0, $"No session blocks exist on '{targetDate}' to finalize."));

        var target = sessionNumber is { } num
            ? day!.Sessions.FirstOrDefault(s => s.SessionNumber == num)
            : day!.Sessions.LastOrDefault(s => s.Status == SessionBlockStatus.Active) ?? day.Sessions[^1];
        await Check(
            "FinalizeSession",
            IntoChat.OS.UI.Decision.Rule("session_block_exists", target, static t => t is null, $"Session #{sessionNumber} was not found on '{targetDate}'."));

        var finalThreads = openThreads is not null
            ? MergeItems([], await MaskItemsAsync(openThreads).ConfigureAwait(true))
            : target!.OpenThreads;

        var finalized = target! with
        {
            Status = SessionBlockStatus.Finalized,
            OpenThreads = finalThreads,
            UpdatedAt = now,
            EndedAt = now,
        };

        var updatedSessions = ReplaceSession(day!.Sessions, finalized);
        await SaveDayAsync(new SessionJournalDay(targetDate, updatedSessions), finalized).ConfigureAwait(true);
        return finalized;
    }

    public async Task FollowAgent(string agentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var relative = NormalizeAgentKey(agentKey);
        await Context.Wires.Ensure<TurnCompleted>(new NeuronId(Id.Brain, AgentSignals.NeuronType, relative)).ConfigureAwait(true);
    }

    [On]
    public async Task OnTurnCompleted(TurnCompleted signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.Reply))
        {
            return;
        }

        var milestone = ExtractMilestoneFromTurn(signal.AgentId, userText: null, signal.Reply);
        await RecordMilestone(milestone).ConfigureAwait(true);
    }

    public Task<SessionJournalDay?> ReadDay(string? date = null)
    {
        var targetDate = string.IsNullOrWhiteSpace(date) ? FormatDate(Context.Time.GetUtcNow()) : date.Trim();
        return Task.FromResult(FindDay(State, targetDate));
    }

    public Task<SessionReturningView> ReadReturningView(string? todayDate = null)
    {
        var today = string.IsNullOrWhiteSpace(todayDate) ? FormatDate(Context.Time.GetUtcNow()) : todayDate.Trim();
        var todayDay = FindDay(State, today);
        if (todayDay is { Sessions.Length: > 0 })
        {
            return Task.FromResult(BuildReturningView(State.Revision, todayDay, isPriorDay: false));
        }

        var priorDay = State.Days
            .Where(d => d.Sessions.Length > 0 && string.CompareOrdinal(d.Date, today) < 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .FirstOrDefault();

        if (priorDay is not null)
        {
            return Task.FromResult(BuildReturningView(State.Revision, priorDay, isPriorDay: true));
        }

        return Task.FromResult(new SessionReturningView { Revision = State.Revision });
    }

    public Task<string[]> ListDates()
        => Task.FromResult(State.Days.Select(d => d.Date).OrderBy(d => d, StringComparer.Ordinal).ToArray());

    internal static SessionMilestone ExtractMilestoneFromTurn(string? conversationId, string? userText, string? replyText)
    {
        string? goal = null;
        if (!string.IsNullOrWhiteSpace(userText))
        {
            var trimmedUser = CollapseLine(userText);
            if (!trimmedUser.StartsWith('/'))
            {
                goal = Truncate(trimmedUser, 180);
            }
        }

        var deliverables = new List<string>();
        var decisions = new List<string>();
        var openThreads = new List<string>();

        foreach (var rawLine in (replyText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.TrimStart('-', '*', ' ');
            if (line.Length < 6)
            {
                continue;
            }

            if (line.StartsWith("Deliverable:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Created ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Saved ", StringComparison.OrdinalIgnoreCase))
            {
                deliverables.Add(Truncate(line, 200));
            }
            else if (line.StartsWith("Decision:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Decided ", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(Truncate(line, 200));
            }
            else if (line.StartsWith("Open thread:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Next step:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("TODO:", StringComparison.OrdinalIgnoreCase))
            {
                openThreads.Add(Truncate(line, 200));
            }
        }

        return new SessionMilestone
        {
            ConversationId = conversationId,
            Goal = goal,
            Deliverables = deliverables.Count > 0 ? [.. deliverables] : null,
            Decisions = decisions.Count > 0 ? [.. decisions] : null,
            OpenThreads = openThreads.Count > 0 ? [.. openThreads] : null,
        };
    }

    private async Task SaveDayAsync(SessionJournalDay updatedDay, SessionJournalBlock changedBlock)
    {
        var days = State.Days
            .Where(d => !string.Equals(d.Date, updatedDay.Date, StringComparison.Ordinal))
            .Append(updatedDay)
            .OrderBy(d => d.Date, StringComparer.Ordinal)
            .TakeLast(MaxKeptDays)
            .ToArray();

        var next = new SessionJournalState
        {
            Revision = State.Revision + 1,
            Days = days,
        };

        await Save(
            next,
            new SessionJournalUpdated(
                Id.FullKey(),
                next.Revision,
                updatedDay.Date,
                changedBlock.SessionNumber,
                changedBlock.Status)).ConfigureAwait(true);
    }

    private async Task<string> ResolveDateAsync(string operation, string? date, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return FormatDate(now);
        }

        var trimmed = date.Trim();
        await Check(
            operation,
            IntoChat.OS.UI.Decision.Rule(
                "valid_iso_date",
                trimmed,
                static d => !IsoDatePattern().IsMatch(d) || !DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                $"Date '{trimmed}' must use YYYY-MM-DD format."));
        return trimmed;
    }

    private string NormalizeAgentKey(string agentKey)
    {
        var trimmed = agentKey.Trim();
        var prefix = Id.Brain.Value + "/";
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..] : trimmed;
    }

    private async Task<string> MaskAsync(string text)
        => redactor is null ? text : (await redactor.Redact(text, CancellationToken.None).ConfigureAwait(true)) ?? text;

    private async Task<string[]> MaskItemsAsync(string[]? items)
    {
        if (items is null || items.Length == 0)
        {
            return [];
        }

        var list = new List<string>(items.Length);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                list.Add(CollapseLine(await MaskAsync(item).ConfigureAwait(true)));
            }
        }
        return [.. list];
    }

    private static SessionJournalDay? FindDay(SessionJournalState state, string date)
        => state.Days.FirstOrDefault(d => string.Equals(d.Date, date, StringComparison.Ordinal));

    private static SessionJournalBlock? ResolveTargetBlock(SessionJournalDay day, int? sessionNumber, string? conversationId)
    {
        if (sessionNumber is { } num)
        {
            return day.Sessions.FirstOrDefault(s => s.SessionNumber == num);
        }

        if (conversationId is not null)
        {
            var matching = day.Sessions.LastOrDefault(s => string.Equals(s.ConversationId, conversationId, StringComparison.Ordinal) && s.Status == SessionBlockStatus.Active);
            if (matching is not null)
            {
                return matching;
            }
            if (day.Sessions.Any(s => string.Equals(s.ConversationId, conversationId, StringComparison.Ordinal)))
            {
                return null;
            }
        }

        return day.Sessions.LastOrDefault(s => s.Status == SessionBlockStatus.Active);
    }

    private static SessionJournalBlock[] ReplaceSession(SessionJournalBlock[] sessions, SessionJournalBlock updated)
        => [.. sessions.Select(s => s.SessionNumber == updated.SessionNumber ? updated : s)];

    private static SessionJournalBlock[] ReplaceOrAppendSession(SessionJournalBlock[] sessions, SessionJournalBlock updated)
        => sessions.Any(s => s.SessionNumber == updated.SessionNumber)
            ? ReplaceSession(sessions, updated)
            : [.. sessions.OrderBy(s => s.SessionNumber), updated];

    private static string[] MergeItems(string[] existing, string[] added)
    {
        if (added.Length == 0)
        {
            return existing;
        }

        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(existing);
        foreach (var item in added)
        {
            if (item.Length > 0 && seen.Add(item))
            {
                merged.Add(item);
            }
        }
        return [.. merged.TakeLast(MaxItemsPerSection)];
    }

    private static SessionReturningView BuildReturningView(long revision, SessionJournalDay day, bool isPriorDay)
    {
        var ordered = day.Sessions.OrderBy(s => s.SessionNumber).ToArray();
        var activeProject = ordered.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.ProjectBrief))?.ProjectBrief;
        var openThreads = ordered.SelectMany(s => s.OpenThreads).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new SessionReturningView
        {
            Revision = revision,
            SourceDate = day.Date,
            IsPriorDay = isPriorDay,
            ActiveProjectBrief = activeProject,
            OpenThreads = openThreads,
            Sessions = ordered,
            FormattedMarkdown = $"# Daily Memory ({day.Date})\n\n{day.FormatMarkdown()}",
        };
    }

    internal static string FormatDate(DateTimeOffset utc)
        => utc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CollapseLine(string text)
        => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";
}
