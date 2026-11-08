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

    // Pasted links (added field; absent in state saved before it): a link's app and opaque input, by the request its post was
    // made under, newest last, at most ChatAppCard.MaxLinksKept. They lived in a text neuron beside the conversations before;
    // that store is not migrated.
    public InoLink[] Links { get; init; } = [];
}

// One pasted link; serialised as the chat's link store reads it (Request is the post's request id, "N" format).
public sealed record InoLink(string Request, string App, string Input);
