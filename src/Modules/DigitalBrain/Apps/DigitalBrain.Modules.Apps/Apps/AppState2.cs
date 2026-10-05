namespace DigitalBrain.Apps;

// Renamed to AppState in the deletion sweep, when the Workspace grain's state goes.
[GenerateSerializer, Alias("apps.state2")]
public sealed class AppStateV2
{
    [Id(0)] public AppManifest? Manifest { get; set; }
    [Id(1)] public string Spec { get; set; } = "";
    [Id(2)] public Dictionary<string, string> Files { get; set; } = [];
    [Id(3)] public Dictionary<string, string> Settings { get; set; } = [];
    [Id(4)] public Dictionary<string, string> Connections { get; set; } = [];
    [Id(5)] public string Version { get; set; } = "0.0.0";
    [Id(6)] public AppStatusV2 Status { get; set; }
    [Id(7)] public string[] Activity { get; set; } = [];
    [Id(8)] public AppVersionRecord[] History { get; set; } = [];
    [Id(9)] public AppScenarioVerdict[] Verdicts { get; set; } = [];
    [Id(10)] public Dictionary<string, bool> Verified { get; set; } = [];
    [Id(11)] public Guid BuildAttempt { get; set; }
    [Id(12)] public DateTimeOffset BuildStartedAt { get; set; }
    [Id(13)] public string? BrainScopeId { get; set; }
    [Id(14)] public int ProgramGeneration { get; set; }
    [Id(15)] public string[] StorageFiles { get; set; } = [];
    [Id(16)] public string[] DeployedFiles { get; set; } = [];
    [Id(17)] public Dictionary<string, string> BehaviorErrors { get; set; } = [];
    [Id(18)] public List<AppInvocation> Invocations { get; set; } = [];
    [Id(19)] public List<OperationReceipt> Receipts { get; set; } = [];

    public AppStateV2 Clone() => new()
    {
        Manifest = Manifest,
        Spec = Spec,
        Files = new(Files, StringComparer.Ordinal),
        Settings = new(Settings, StringComparer.Ordinal),
        Connections = new(Connections, StringComparer.Ordinal),
        Version = Version,
        Status = Status,
        Activity = [.. Activity],
        History = [.. History],
        Verdicts = [.. Verdicts],
        Verified = new(Verified, StringComparer.Ordinal),
        BuildAttempt = BuildAttempt,
        BuildStartedAt = BuildStartedAt,
        BrainScopeId = BrainScopeId,
        ProgramGeneration = ProgramGeneration,
        StorageFiles = [.. StorageFiles],
        DeployedFiles = [.. DeployedFiles],
        BehaviorErrors = new(BehaviorErrors, StringComparer.Ordinal),
        Invocations = [.. Invocations],
        Receipts = [.. Receipts],
    };
}

[GenerateSerializer, Alias("apps.version-record")]
public sealed record AppVersionRecord(
    [property: Id(0)] string Version,
    [property: Id(1)] DateTimeOffset SavedAt,
    [property: Id(2)] AppManifest Manifest,
    [property: Id(3)] string Spec,
    [property: Id(4)] Dictionary<string, string> Files);
