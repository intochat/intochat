using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class LoopLessons
{
    public const string Category = "repo:os";

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
            $"lesson:{cleanKey}",
            $"Lesson: {cleanKey}",
            Category,
            briefContent);

        var scratchpadNote = InoNativeHarness.CollapseSingleLine(
            $"Lesson [{cleanKey}]: tried {cleanTried}; failed because {cleanReason}",
            220);
        await memory.UpsertScratchpad(
            WorkingMemorySection.EnvironmentNotes,
            scratchpadNote,
            replaceMatching: cleanKey);
    }
}
