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
