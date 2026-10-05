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
}

[GenerateSerializer]
public sealed record AppDeployment([property: Id(0)] AppState Target);
