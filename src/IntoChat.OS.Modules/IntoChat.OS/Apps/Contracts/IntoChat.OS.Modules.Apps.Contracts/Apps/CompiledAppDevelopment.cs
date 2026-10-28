namespace IntoChat.OS.Modules.Apps;

public sealed record CompiledAppDevelopmentLease(string Token, string Brain,
    string Name, string Project, DateTimeOffset ExpiresAt);

public sealed record CompiledAppReload(string LeaseToken, string Project,
    string Digest, long ExpectedRevision, Guid OperationId);
