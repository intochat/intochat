using System.ComponentModel;
using System.Text;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Memory;

[GenerateSerializer, Alias("ai.working-memory.section")]
public enum WorkingMemorySection
{
    ActiveThreads = 0,
    EnvironmentNotes = 1,
    PendingDecisions = 2,
}

[GenerateSerializer, Alias("ai.working-memory.slice-kind")]
public enum WorkingMemorySliceKind
{
    Soul = 0,
    User = 1,
    Project = 2,
}

[GenerateSerializer, Alias("ai.working-memory.scratchpad")]
public sealed record WorkingMemoryScratchpad
{
    public const int MaxCharacters = 2_500;

    [Id(0)] public string[] ActiveThreads { get; init; } = [];
    [Id(1)] public string[] EnvironmentNotes { get; init; } = [];
    [Id(2)] public string[] PendingDecisions { get; init; } = [];

    public WorkingMemoryScratchpad() { }

    public WorkingMemoryScratchpad(string[] activeThreads, string[] environmentNotes, string[] pendingDecisions)
    {
        ActiveThreads = activeThreads ?? [];
        EnvironmentNotes = environmentNotes ?? [];
        PendingDecisions = pendingDecisions ?? [];
    }

    public bool IsEmpty => ActiveThreads.Length == 0 && EnvironmentNotes.Length == 0 && PendingDecisions.Length == 0;

    public int CharacterCount => FormatMarkdown().Length;

    public string FormatMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Working Memory");
        sb.AppendLine();
        AppendSection(sb, "Active Threads", ActiveThreads);
        sb.AppendLine();
        AppendSection(sb, "Environment Notes", EnvironmentNotes);
        sb.AppendLine();
        AppendSection(sb, "Pending Decisions", PendingDecisions);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string heading, string[] items)
    {
        sb.Append("## ").AppendLine(heading);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                sb.Append("- ").AppendLine(item.Trim());
            }
        }
    }
}

[GenerateSerializer, Alias("ai.working-memory.slice")]
public sealed record WorkingMemorySlice
{
    [Id(0)] public WorkingMemorySliceKind Kind { get; init; }
    [Id(1)] public string Content { get; init; } = "";
    [Id(2)] public DateTimeOffset UpdatedAt { get; init; }

    public WorkingMemorySlice() { }

    public WorkingMemorySlice(WorkingMemorySliceKind kind, string content, DateTimeOffset updatedAt)
    {
        Kind = kind;
        Content = content ?? "";
        UpdatedAt = updatedAt;
    }
}

[GenerateSerializer, Alias("ai.working-memory.brief")]
public sealed record ContextBrief
{
    [Id(0)] public string Key { get; init; } = "";
    [Id(1)] public string Title { get; init; } = "";
    [Id(2)] public string Category { get; init; } = "project";
    [Id(3)] public string Content { get; init; } = "";
    [Id(4)] public DateTimeOffset UpdatedAt { get; init; }

    public ContextBrief() { }

    public ContextBrief(string key, string title, string category, string content, DateTimeOffset updatedAt)
    {
        Key = key;
        Title = title;
        Category = category;
        Content = content;
        UpdatedAt = updatedAt;
    }
}

[GenerateSerializer, Alias("ai.working-memory.brief-summary")]
public sealed record ContextBriefSummary
{
    [Id(0)] public string Key { get; init; } = "";
    [Id(1)] public string Title { get; init; } = "";
    [Id(2)] public string Category { get; init; } = "project";
    [Id(3)] public int CharacterCount { get; init; }
    [Id(4)] public DateTimeOffset UpdatedAt { get; init; }

    public ContextBriefSummary() { }

    public ContextBriefSummary(string key, string title, string category, int characterCount, DateTimeOffset updatedAt)
    {
        Key = key;
        Title = title;
        Category = category;
        CharacterCount = characterCount;
        UpdatedAt = updatedAt;
    }
}

[GenerateSerializer, Alias("ai.working-memory.startup-pack")]
public sealed record WorkingMemoryStartupPack
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public string Soul { get; init; } = "";
    [Id(2)] public string User { get; init; } = "";
    [Id(3)] public string? Project { get; init; }
    [Id(4)] public WorkingMemoryScratchpad Scratchpad { get; init; } = new();
    [Id(5)] public ContextBriefSummary[] AvailableBriefs { get; init; } = [];
    [Id(6)] public string FormattedPrompt { get; init; } = "";
    [Id(7)] public string? FrozenSessionId { get; init; }
}

[GenerateSerializer, Alias("ai.working-memory.snapshot")]
public sealed record WorkingMemorySnapshot
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public WorkingMemorySlice[] Slices { get; init; } = [];
    [Id(2)] public WorkingMemoryScratchpad LiveScratchpad { get; init; } = new();
    [Id(3)] public WorkingMemoryScratchpad? FrozenScratchpad { get; init; }
    [Id(4)] public string? FrozenSessionId { get; init; }
    [Id(5)] public ContextBriefSummary[] Briefs { get; init; } = [];
}

[DigitalBrain.Signal("ai.working-memory.changed")]
public sealed record WorkingMemoryChanged(
    string MemoryId,
    long Revision,
    int ScratchpadCharacters,
    int BriefCount) : DigitalBrain.Signal;

[NeuronAlias(NeuronType), DefaultNeuronType(NeuronType)]
[Description("Compact pinned working scratchpad (~2.5 KB cap with ActiveThreads, EnvironmentNotes, PendingDecisions), SOUL/USER/PROJECT slices, and on-demand context briefs.")]
public interface IWorkingMemory
{
    const string NeuronType = "ai.working-memory";
    const string DefaultKey = "default";

    static NeuronId NeuronIdOf(BrainId brain, string key = DefaultKey) => new(brain, NeuronType, key);

    static string Key(BrainId brain, string key = DefaultKey) => NeuronIdOf(brain, key).FullKey();

    [ReadOnlyCall, Interleaves]
    Task<WorkingMemorySnapshot> Read();

    [Description("Read the minimal startup context pack (SOUL, USER, PROJECT, frozen 2.5 KB scratchpad, and brief catalog index without dumping brief bodies).")]
    [ReadOnlyCall, Interleaves]
    Task<WorkingMemoryStartupPack> ReadStartupPack();

    [Description("Freeze the live working scratchpad for a session so mid-session writes apply on the next session without invalidating the prompt prefix.")]
    Task<WorkingMemoryStartupPack> FreezeSession(string sessionId);

    [Description("Add or replace a bullet in ActiveThreads, EnvironmentNotes, or PendingDecisions within the 2,500-character scratchpad cap.")]
    Task<WorkingMemoryScratchpad> UpsertScratchpad(WorkingMemorySection section, string entry, string? replaceMatching = null);

    [Description("Remove matching entries from ActiveThreads, EnvironmentNotes, or PendingDecisions.")]
    Task<WorkingMemoryScratchpad> RemoveScratchpad(WorkingMemorySection section, string match);

    [Description("Replace the entire working scratchpad (must fit within the 2,500-character cap).")]
    Task<WorkingMemoryScratchpad> SetScratchpad(WorkingMemoryScratchpad scratchpad);

    [Description("Set the SOUL, USER, or PROJECT context slice.")]
    Task<WorkingMemorySlice> SetSlice(WorkingMemorySliceKind kind, string content);

    [ReadOnlyCall, Interleaves]
    Task<WorkingMemorySlice?> GetSlice(WorkingMemorySliceKind kind);

    [Description("Store or update a named context brief that agents lazy-load on demand.")]
    Task<ContextBriefSummary> PutBrief(string key, string title, string category, string content);

    [Description("Delete a named context brief.")]
    Task<bool> RemoveBrief(string key);

    [Description("Lazy-load the full body of a single context brief by key.")]
    [ReadOnlyCall, Interleaves]
    Task<ContextBrief?> GetBrief(string key);

    [Description("List available context brief summaries without loading their full bodies.")]
    [ReadOnlyCall, Interleaves]
    Task<ContextBriefSummary[]> ListBriefs(string? category = null);
}
