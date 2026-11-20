using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.State;
using DigitalBrain.Capabilities.Secrets;

namespace IntoChat.OS.Modules.AI.Memory;

[GenerateSerializer, Alias("ai.working-memory.state")]
[ExportableState]
internal sealed record WorkingMemoryState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public WorkingMemorySlice[] Slices { get; init; } = [];
    [Id(2)] public WorkingMemoryScratchpad LiveScratchpad { get; init; } = new();
    [Id(3)] public WorkingMemoryScratchpad? FrozenScratchpad { get; init; }
    [Id(4)] public string? FrozenSessionId { get; init; }
    [Id(5)] public ContextBrief[] Briefs { get; init; } = [];
}

[Neuron(IWorkingMemory.NeuronType, State = "working-memory")]
internal sealed partial class WorkingMemoryNeuron(ISecretRedactor? redactor = null)
    : Neuron<WorkingMemoryState>, IWorkingMemory
{
    internal const int MaxSoulCharacters = 3_000;
    internal const int MaxUserCharacters = 2_000;
    internal const int MaxProjectCharacters = 2_000;
    internal const int MaxBriefCharacters = 16_000;
    internal const int MaxBriefCount = 128;

    [GeneratedRegex(@"(?:\bsk-(?:ant-|or-|proj-|live_)[A-Za-z0-9_\-]{12,}|\bAIza[0-9A-Za-z\-_]{20,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bxox[baprs]-[A-Za-z0-9\-]{12,})", RegexOptions.CultureInvariant)]
    private static partial Regex RawSecretLiteral();

    public Task<WorkingMemorySnapshot> Read()
        => Task.FromResult(new WorkingMemorySnapshot
        {
            Revision = State.Revision,
            Slices = [.. State.Slices],
            LiveScratchpad = State.LiveScratchpad,
            FrozenScratchpad = State.FrozenScratchpad,
            FrozenSessionId = State.FrozenSessionId,
            Briefs = [.. State.Briefs.Select(ToSummary)],
        });

    public Task<WorkingMemoryStartupPack> ReadStartupPack()
        => Task.FromResult(BuildStartupPack(State));

    public async Task<WorkingMemoryStartupPack> FreezeSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalized = sessionId.Trim();
        if (string.Equals(State.FrozenSessionId, normalized, StringComparison.Ordinal) && State.FrozenScratchpad is not null)
        {
            return BuildStartupPack(State);
        }

        var next = State with
        {
            Revision = State.Revision + 1,
            FrozenScratchpad = State.LiveScratchpad,
            FrozenSessionId = normalized,
        };
        await PersistAsync(next).ConfigureAwait(false);
        return BuildStartupPack(next);
    }

    public async Task<WorkingMemoryScratchpad> UpsertScratchpad(WorkingMemorySection section, string entry, string? replaceMatching = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        await Check("UpsertScratchpad", NoRawSecretRule(entry));
        var clean = CollapseSingleLine(await MaskAsync(entry).ConfigureAwait(false));
        var currentItems = ItemsOf(State.LiveScratchpad, section);
        var updated = UpsertItems(currentItems, clean, replaceMatching);
        var candidate = Consolidate(WithSection(State.LiveScratchpad, section, updated));
        await Check("UpsertScratchpad", ScratchpadCapRule(candidate));

        var next = State with { Revision = State.Revision + 1, LiveScratchpad = candidate };
        await PersistAsync(next).ConfigureAwait(false);
        return candidate;
    }

    public async Task<WorkingMemoryScratchpad> RemoveScratchpad(WorkingMemorySection section, string match)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(match);
        var needle = match.Trim();
        var currentItems = ItemsOf(State.LiveScratchpad, section);
        var filtered = currentItems
            .Where(item => !item.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (filtered.Length == currentItems.Length)
        {
            return State.LiveScratchpad;
        }

        var candidate = WithSection(State.LiveScratchpad, section, filtered);
        var next = State with { Revision = State.Revision + 1, LiveScratchpad = candidate };
        await PersistAsync(next).ConfigureAwait(false);
        return candidate;
    }

    public async Task<WorkingMemoryScratchpad> SetScratchpad(WorkingMemoryScratchpad scratchpad)
    {
        ArgumentNullException.ThrowIfNull(scratchpad);
        var rawCombined = string.Join('\n', scratchpad.ActiveThreads.Concat(scratchpad.EnvironmentNotes).Concat(scratchpad.PendingDecisions));
        await Check("SetScratchpad", NoRawSecretRule(rawCombined));

        var masked = new WorkingMemoryScratchpad(
            await MaskLinesAsync(scratchpad.ActiveThreads).ConfigureAwait(false),
            await MaskLinesAsync(scratchpad.EnvironmentNotes).ConfigureAwait(false),
            await MaskLinesAsync(scratchpad.PendingDecisions).ConfigureAwait(false));
        var consolidated = Consolidate(masked);
        await Check("SetScratchpad", ScratchpadCapRule(consolidated));

        var next = State with { Revision = State.Revision + 1, LiveScratchpad = consolidated };
        await PersistAsync(next).ConfigureAwait(false);
        return consolidated;
    }

    public async Task<WorkingMemorySlice> SetSlice(WorkingMemorySliceKind kind, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        await Check("SetSlice", NoRawSecretRule(content));
        var masked = (await MaskAsync(content).ConfigureAwait(false)).Trim();
        var cap = MaxSliceChars(kind);
        await Check(
            "SetSlice",
            IntoChat.OS.UI.Decision.Rule(
                "slice_cap_exceeded",
                (Text: masked, Cap: cap, Kind: kind),
                static s => s.Text.Length > s.Cap,
                $"Slice '{kind}' is {masked.Length} characters; cap is {cap} characters."));

        var slice = new WorkingMemorySlice(kind, masked, Context.Time.GetUtcNow());
        var slices = State.Slices.Where(existing => existing.Kind != kind).Append(slice).OrderBy(s => s.Kind).ToArray();
        var next = State with { Revision = State.Revision + 1, Slices = slices };
        await PersistAsync(next).ConfigureAwait(false);
        return slice;
    }

    public Task<WorkingMemorySlice?> GetSlice(WorkingMemorySliceKind kind)
        => Task.FromResult(State.Slices.FirstOrDefault(s => s.Kind == kind));

    public async Task<ContextBriefSummary> PutBrief(string key, string title, string category, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        await Check("PutBrief", NoRawSecretRule(content));

        var normalizedKey = key.Trim();
        var normalizedTitle = title.Trim();
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "project" : category.Trim().ToLowerInvariant();
        var maskedContent = (await MaskAsync(content).ConfigureAwait(false)).Trim();
        await Check(
            "PutBrief",
            IntoChat.OS.UI.Decision.Rule(
                "brief_key_too_long",
                normalizedKey,
                static k => k.Length > 120,
                "Context brief key must be at most 120 characters."),
            IntoChat.OS.UI.Decision.Rule(
                "brief_cap_exceeded",
                maskedContent,
                static c => c.Length > MaxBriefCharacters,
                $"Context brief '{normalizedKey}' is {maskedContent.Length} characters; cap is {MaxBriefCharacters}."));

        var brief = new ContextBrief(normalizedKey, normalizedTitle, normalizedCategory, maskedContent, Context.Time.GetUtcNow());
        var updated = State.Briefs
            .Where(existing => !string.Equals(existing.Key, normalizedKey, StringComparison.OrdinalIgnoreCase))
            .Append(brief)
            .TakeLast(MaxBriefCount)
            .ToArray();

        var next = State with { Revision = State.Revision + 1, Briefs = updated };
        await PersistAsync(next).ConfigureAwait(false);
        return ToSummary(brief);
    }

    public async Task<bool> RemoveBrief(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var trimmed = key.Trim();
        var filtered = State.Briefs
            .Where(existing => !string.Equals(existing.Key, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (filtered.Length == State.Briefs.Length)
        {
            return false;
        }

        var next = State with { Revision = State.Revision + 1, Briefs = filtered };
        await PersistAsync(next).ConfigureAwait(false);
        return true;
    }

    public Task<ContextBrief?> GetBrief(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult<ContextBrief?>(null);
        }

        var trimmed = key.Trim();
        return Task.FromResult(State.Briefs.FirstOrDefault(b => string.Equals(b.Key, trimmed, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<ContextBriefSummary[]> ListBriefs(string? category = null)
    {
        var query = State.Briefs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            var filter = category.Trim();
            query = query.Where(b => string.Equals(b.Category, filter, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(query.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).Select(ToSummary).ToArray());
    }

    private Task PersistAsync(WorkingMemoryState next)
        => Save(
            next,
            new WorkingMemoryChanged(
                Id.FullKey(),
                next.Revision,
                next.LiveScratchpad.CharacterCount,
                next.Briefs.Length));

    private async Task<string> MaskAsync(string text)
        => redactor is null ? text : (await redactor.Redact(text, CancellationToken.None).ConfigureAwait(false)) ?? text;

    private async Task<string[]> MaskLinesAsync(string[]? lines)
    {
        if (lines is null || lines.Length == 0)
        {
            return [];
        }

        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                result.Add(CollapseSingleLine(await MaskAsync(line).ConfigureAwait(false)));
            }
        }
        return [.. result];
    }

    private static Rule NoRawSecretRule(string text)
        => IntoChat.OS.UI.Decision.Rule(
            "no_secret_values",
            text,
            static t => RawSecretLiteral().IsMatch(t),
            "Never store raw secret or API key values in working memory; reference secret or environment variable names only.");

    private static Rule ScratchpadCapRule(WorkingMemoryScratchpad scratchpad)
        => IntoChat.OS.UI.Decision.Rule(
            "scratchpad_cap_exceeded",
            scratchpad,
            static pad => pad.CharacterCount > WorkingMemoryScratchpad.MaxCharacters,
            $"Working memory scratchpad is {scratchpad.CharacterCount} characters; consolidate or remove entries to stay within the {WorkingMemoryScratchpad.MaxCharacters}-character cap.");

    private static int MaxSliceChars(WorkingMemorySliceKind kind) => kind switch
    {
        WorkingMemorySliceKind.Soul => MaxSoulCharacters,
        WorkingMemorySliceKind.User => MaxUserCharacters,
        WorkingMemorySliceKind.Project => MaxProjectCharacters,
        _ => MaxUserCharacters,
    };

    private static string[] ItemsOf(WorkingMemoryScratchpad pad, WorkingMemorySection section) => section switch
    {
        WorkingMemorySection.ActiveThreads => pad.ActiveThreads,
        WorkingMemorySection.EnvironmentNotes => pad.EnvironmentNotes,
        WorkingMemorySection.PendingDecisions => pad.PendingDecisions,
        _ => pad.ActiveThreads,
    };

    private static WorkingMemoryScratchpad WithSection(WorkingMemoryScratchpad pad, WorkingMemorySection section, string[] items) => section switch
    {
        WorkingMemorySection.ActiveThreads => pad with { ActiveThreads = items },
        WorkingMemorySection.EnvironmentNotes => pad with { EnvironmentNotes = items },
        WorkingMemorySection.PendingDecisions => pad with { PendingDecisions = items },
        _ => pad,
    };

    private static string[] UpsertItems(string[] current, string entry, string? replaceMatching)
    {
        var list = new List<string>(current.Length + 1);
        var replaced = false;
        foreach (var existing in current)
        {
            if (!string.IsNullOrWhiteSpace(replaceMatching)
                && existing.Contains(replaceMatching.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (!replaced)
                {
                    list.Add(entry);
                    replaced = true;
                }
                continue;
            }

            if (string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase))
            {
                if (!replaced)
                {
                    list.Add(entry);
                    replaced = true;
                }
                continue;
            }

            list.Add(existing);
        }

        if (!replaced)
        {
            list.Add(entry);
        }

        return [.. list];
    }

    private static WorkingMemoryScratchpad Consolidate(WorkingMemoryScratchpad pad)
        => new(
            DeduplicateSection(pad.ActiveThreads),
            DeduplicateSection(pad.EnvironmentNotes),
            DeduplicateSection(pad.PendingDecisions));

    private static string[] DeduplicateSection(string[] items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(items.Length);
        foreach (var raw in items)
        {
            var clean = CollapseSingleLine(raw);
            if (clean.Length > 0 && seen.Add(clean))
            {
                result.Add(clean);
            }
        }
        return [.. result];
    }

    private static string CollapseSingleLine(string text)
        => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static ContextBriefSummary ToSummary(ContextBrief brief)
        => new(brief.Key, brief.Title, brief.Category, brief.Content.Length, brief.UpdatedAt);

    private static WorkingMemoryStartupPack BuildStartupPack(WorkingMemoryState state)
    {
        var soul = state.Slices.FirstOrDefault(s => s.Kind == WorkingMemorySliceKind.Soul)?.Content ?? "";
        var user = state.Slices.FirstOrDefault(s => s.Kind == WorkingMemorySliceKind.User)?.Content ?? "";
        var project = state.Slices.FirstOrDefault(s => s.Kind == WorkingMemorySliceKind.Project)?.Content;
        var activePad = state.FrozenScratchpad ?? state.LiveScratchpad;
        var briefs = state.Briefs.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).Select(ToSummary).ToArray();

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(soul))
        {
            sb.AppendLine("[SOUL]");
            sb.AppendLine(soul);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(user))
        {
            sb.AppendLine("[USER]");
            sb.AppendLine(user);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(project))
        {
            sb.AppendLine("[PROJECT]");
            sb.AppendLine(project);
            sb.AppendLine();
        }
        if (!activePad.IsEmpty)
        {
            sb.AppendLine(activePad.FormatMarkdown());
            sb.AppendLine();
        }
        if (briefs.Length > 0)
        {
            sb.AppendLine("[Lazy Context Briefs — call IWorkingMemory.GetBrief(key) on demand]");
            foreach (var b in briefs)
            {
                sb.Append("- ").Append(b.Key).Append(" (").Append(b.Category).Append(", ").Append(b.CharacterCount).Append(" chars): ").AppendLine(b.Title);
            }
        }

        return new WorkingMemoryStartupPack
        {
            Revision = state.Revision,
            Soul = soul,
            User = user,
            Project = string.IsNullOrWhiteSpace(project) ? null : project,
            Scratchpad = activePad,
            AvailableBriefs = briefs,
            FormattedPrompt = sb.ToString().TrimEnd(),
            FrozenSessionId = state.FrozenSessionId,
        };
    }
}
