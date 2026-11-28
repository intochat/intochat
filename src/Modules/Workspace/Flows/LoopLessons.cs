using System.Text;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record LoopLessonContext(string? PrimaryLessonKey, string PromptSection);

internal static class LoopLessons
{
    public const string Category = "repo:os";
    public const string LessonKeyPrefix = "lesson:";

    public static async Task<LoopLessonContext> FindRelevantLessonsAsync(
        IWorkingMemory memory,
        LoopFinding finding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(finding);
        ct.ThrowIfCancellationRequested();

        var matched = new List<ContextBrief>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var exactKey = $"{LessonKeyPrefix}{finding.Key.Trim()}";
        var exactBrief = await memory.GetBrief(exactKey).ConfigureAwait(true);
        if (exactBrief is not null && !string.IsNullOrWhiteSpace(exactBrief.Content))
        {
            matched.Add(exactBrief);
            seenKeys.Add(exactBrief.Key);
        }

        var searchTerms = ExtractKeywords($"{finding.Key} {finding.Title} {finding.Detail}");
        if (searchTerms.Count > 0)
        {
            var briefs = await memory.ListBriefs(Category).ConfigureAwait(true);
            foreach (var summary in briefs)
            {
                ct.ThrowIfCancellationRequested();
                if (matched.Count >= 3)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(summary.Key)
                    || !summary.Key.StartsWith(LessonKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    || !seenKeys.Add(summary.Key))
                {
                    continue;
                }

                var haystack = $"{summary.Key} {summary.Title}".ToLowerInvariant();
                if (!searchTerms.Any(term => haystack.Contains(term, StringComparison.Ordinal)))
                {
                    continue;
                }

                var brief = await memory.GetBrief(summary.Key).ConfigureAwait(true);
                if (brief is not null && !string.IsNullOrWhiteSpace(brief.Content))
                {
                    matched.Add(brief);
                }
            }
        }

        if (matched.Count == 0)
        {
            return new LoopLessonContext(null, string.Empty);
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Prior Loop Lessons & Anti-Patterns to Avoid]");
        foreach (var brief in matched)
        {
            sb.AppendLine($"- {brief.Key}: {InoNativeHarness.CollapseSingleLine(brief.Content, 320)}");
        }

        return new LoopLessonContext(matched[0].Key, sb.ToString().TrimEnd());
    }

    public static async Task RecordFailureAsync(
        IWorkingMemory memory,
        string findingKey,
        string whatWasTried,
        string whyItFailed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(whatWasTried);
        ArgumentException.ThrowIfNullOrWhiteSpace(whyItFailed);

        ct.ThrowIfCancellationRequested();

        var cleanKey = findingKey.Trim();
        var cleanTried = InoNativeHarness.CollapseSingleLine(whatWasTried, 240);
        var cleanReason = InoNativeHarness.CollapseSingleLine(whyItFailed, 240);

        var briefContent = $"Finding: {cleanKey}\nTried: {cleanTried}\nFailed: {cleanReason}";
        await memory.PutBrief(
            $"{LessonKeyPrefix}{cleanKey}",
            $"Lesson: {cleanKey}",
            Category,
            briefContent).ConfigureAwait(true);

        var scratchpadNote = InoNativeHarness.CollapseSingleLine(
            $"Lesson [{cleanKey}]: tried {cleanTried}; failed because {cleanReason}",
            220);
        await memory.UpsertScratchpad(
            WorkingMemorySection.EnvironmentNotes,
            scratchpadNote,
            replaceMatching: cleanKey).ConfigureAwait(true);
    }

    public static async Task RecordSuccessAsync(
        IWorkingMemory memory,
        string findingKey,
        string whatWorked,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(whatWorked);

        ct.ThrowIfCancellationRequested();

        var cleanKey = findingKey.Trim();
        var cleanWorked = InoNativeHarness.CollapseSingleLine(whatWorked, 240);

        var briefContent = $"Finding: {cleanKey}\nResolved: {cleanWorked}";
        await memory.PutBrief(
            $"{LessonKeyPrefix}{cleanKey}",
            $"Lesson (Resolved): {cleanKey}",
            Category,
            briefContent).ConfigureAwait(true);

        var scratchpadNote = InoNativeHarness.CollapseSingleLine(
            $"Resolved [{cleanKey}]: {cleanWorked}",
            220);
        await memory.UpsertScratchpad(
            WorkingMemorySection.EnvironmentNotes,
            scratchpadNote,
            replaceMatching: cleanKey).ConfigureAwait(true);
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var token in text.Split([' ', ':', '/', '\\', '.', '-', '_', '\r', '\n', '\t', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 4)
            {
                result.Add(token.ToLowerInvariant());
            }
        }

        return result;
    }
}
