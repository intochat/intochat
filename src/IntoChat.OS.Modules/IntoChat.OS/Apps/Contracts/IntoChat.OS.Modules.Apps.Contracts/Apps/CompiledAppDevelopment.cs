namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("apps.compiled-development-lease")]
public sealed record CompiledAppDevelopmentLease([property: Id(0)] string Token, [property: Id(1)] string Brain,
    [property: Id(2)] string Name, [property: Id(3)] string Project, [property: Id(4)] DateTimeOffset ExpiresAt);

[GenerateSerializer, Alias("apps.compiled-reload")]
public sealed record CompiledAppReload([property: Id(0)] string LeaseToken, [property: Id(1)] string Project,
    [property: Id(2)] string Digest, [property: Id(3)] long ExpectedRevision, [property: Id(4)] Guid OperationId);
