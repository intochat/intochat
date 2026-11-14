using IntoChat.OS.Modules.Apps.Approvals;

namespace IntoChat.OS.Modules.Apps;

public sealed record InoChartPoint(
    string Id,
    string Label,
    double Value);

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status,
    string? SurfaceScopeUri = null,
    string[]? Columns = null,
    string[][]? Rows = null,
    string? ChartKind = null,
    InoChartPoint[]? ChartPoints = null)
{
    public string EffectiveScopeUri => SurfaceScopeUri ?? $"neuron://local/apps/ino#{Id}";
    public bool IsWaiting => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
        && Kind is "approval" or "choice" or "draft" or "secret" or "app-install";
}

public sealed record InoTurn(
    string UserMessage,
    string Reply,
    string RouteHint,
    int RecalledCount);

public sealed record InoActivity(
    string Id,
    string Title,
    string? Result = null,
    string? Error = null);

public sealed record InoMessage(
    string Id,
    string Role,
    string Text,
    string Status = "completed")
{
    public string? Model { get; init; }
    public string? Failure { get; init; }
    public InoActivity[] Activities { get; init; } = [];
    public InoCard[] Cards { get; init; } = [];
}

public sealed record InoPending(
    string Id,
    string Text,
    DateTimeOffset At);

public sealed record InoError(
    string Code,
    string? Detail = null);

public sealed record InoAppState(
    string ConversationId,
    string Title,
    string Status,
    string VoiceStatus,
    InoTurn[] Turns,
    InoCard[]? Cards = null,
    string? ConnectedDatabase = null,
    bool IsPinned = true,
    string? AgentKey = null,
    string? ActiveRunId = null,
    bool Blocked = false,
    string[]? GrantSummaries = null,
    string? BlockedReason = null,
    string[]? SeenProgress = null)
{
    public InoAppState() : this("main", "New chat", "Ready", "idle", [], [], null, true) { }

    public InoCard[] ActiveCards => Cards ?? [];
    public InoMessage[]? Messages { get; init; }
    public long ChatRevision { get; init; }
    public InoPending? Pending { get; init; }
    public InoError? Error { get; init; }
    public Approval[]? Approvals { get; init; }
    public bool Smart { get; init; }
    public string? SelectedModel { get; init; }
    public string? DetectedLocalModel { get; init; }
    public string[]? DetectedLocalModels { get; init; }
}
