namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.app-state")]
public sealed record AppState
{
    [Id(0)] public AppStatus Status { get; init; }
    [Id(1)] public PackageRevisionRef? Revision { get; init; }
    [Id(3)] public List<PackageSetting> Declared { get; init; } = [];
    [Id(4)] public Dictionary<string, string> Settings { get; init; } = [];
    [Id(5)] public List<PackageOperation> Operations { get; init; } = [];
    [Id(6)] public int ProgramGeneration { get; init; }
    [Id(7)] public List<AppInvocation> Invocations { get; init; } = [];
    [Id(8)] public List<OperationReceipt> Receipts { get; init; } = [];
    [Id(9)] public Dictionary<string, string> Accounts { get; init; } = [];
    [Id(10)] public string Runtime { get; init; } = PackageManifest.CSharpRuntime;
    // Ids 11 (ScriptPaths), 15 (StorageHistoryKnown), 16 (LifecycleReceipts), 17 (PostgresScopes),
    // 18 (PendingAbandonStorage) and 19 (AbandonedStorage) are retired; never reuse them.
    [Id(12)] public string[] StorageFiles { get; init; } = [];
    [Id(13)] public AppDeployment? PendingDeployment { get; init; }
    [Id(14)] public UninstallApp? PendingUninstall { get; init; }
    [Id(20)] public string? BrainScopeId { get; init; }
    [Id(21)] public string? InstallerAppId { get; init; }
    // The storage keys the current generation's runtime returned from Deploy.
    [Id(22)] public string[] DeployedFiles { get; init; } = [];
    // The live app's own content: files edited in place, the canonical scenario document,
    // the latest verdicts and any per-behavior deploy failures. Empty for package installs.
    [Id(23)] public Dictionary<string, string> Files { get; init; } = [];
    [Id(24)] public AppAuthoringDocument? Document { get; init; }
    [Id(25)] public AppScenarioVerdict[] Verdicts { get; init; } = [];
    [Id(26)] public Dictionary<string, string> BehaviorErrors { get; init; } = [];
    [Id(27)] public string Title { get; init; } = "";
    [Id(28)] public string Description { get; init; } = "";
}

[GenerateSerializer]
public sealed record AppDeployment([property: Id(0)] AppState Target);
