namespace IntoChat.OS.Modules.Apps;

// Review data is inert. Only the server can turn an authenticated owner's exact approval into activation.
[GenerateSerializer, Alias("apps.compiled-review")]
public sealed record CompiledAppReview(
    [property: Id(0)] string Token,
    [property: Id(1)] string Digest,
    [property: Id(2)] string Brain,
    [property: Id(3)] string Name,
    [property: Id(4)] long ExpectedRevision,
    [property: Id(5)] bool ReplacesExisting,
    [property: Id(6)] DateTimeOffset ExpiresAt,
    [property: Id(7)] AppRequirements Requires,
    [property: Id(8)] string[] HostAdapters)
{
    public const string TrustMessage = "This package runs trusted code with the host process's access. It is not sandboxed. Install only code you trust.";
}

[GenerateSerializer, Alias("apps.compiled-approval")]
public sealed record CompiledAppApproval(
    [property: Id(0)] string ReviewToken,
    [property: Id(1)] string Digest,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] bool TrustCode,
    [property: Id(4)] string RequirementsConsent,
    [property: Id(5)] bool ReplaceExisting);

// Persisted approval evidence. Review tokens stay ephemeral and are never stored with executable generations.
[GenerateSerializer, Alias("apps.compiled-trust-receipt")]
public sealed record CompiledAppTrustReceipt(
    [property: Id(0)] string Digest,
    [property: Id(1)] string Brain,
    [property: Id(2)] string Name,
    [property: Id(3)] long ExpectedRevision,
    [property: Id(4)] Guid OperationId,
    [property: Id(5)] string AccountId,
    [property: Id(6)] string PrincipalId,
    [property: Id(7)] DateTimeOffset ApprovedAt,
    [property: Id(8)] AppRequirements Requires,
    [property: Id(9)] string[] HostAdapters,
    [property: Id(10)] bool ReplacesExisting);
