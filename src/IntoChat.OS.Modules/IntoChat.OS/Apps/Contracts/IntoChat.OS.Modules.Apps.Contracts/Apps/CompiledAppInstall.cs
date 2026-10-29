namespace IntoChat.OS.Modules.Apps;

// Review data is inert. Only the server can turn an authenticated owner's exact approval into activation.
public sealed record CompiledAppReview(
    string Token,
    string Digest,
    string Brain,
    string Name,
    long ExpectedRevision,
    bool ReplacesExisting,
    DateTimeOffset ExpiresAt,
    AppRequirements Requires,
    string[] HostAdapters)
{
    public const string TrustMessage = "This package runs trusted code with the host process's access. It is not sandboxed. Install only code you trust.";
}

public sealed record CompiledAppApproval(
    string ReviewToken,
    string Digest,
    Guid OperationId,
    bool TrustCode,
    string RequirementsConsent,
    bool ReplaceExisting);

// Persisted approval evidence. Review tokens stay ephemeral and are never stored with executable generations.
public sealed record CompiledAppTrustReceipt(
    string Digest,
    string Brain,
    string Name,
    long ExpectedRevision,
    Guid OperationId,
    string AccountId,
    string PrincipalId,
    DateTimeOffset ApprovedAt,
    AppRequirements Requires,
    string[] HostAdapters,
    bool ReplacesExisting);
