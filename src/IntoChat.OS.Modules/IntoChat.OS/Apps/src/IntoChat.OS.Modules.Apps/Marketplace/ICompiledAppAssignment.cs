namespace IntoChat.OS.Modules.Apps;

/// <summary>The owner-approved installation an app currently runs.</summary>
internal sealed record CompiledAppInstallation(string Digest, long Revision, Guid OperationId, bool DispatchReady, CompiledAppTrustReceipt Receipt);

/// <summary>
/// The seam between owner-reviewed package authorization and execution. Authorization (trust, review, consent, bindings)
/// is complete and enforced before this is called; the implementation only hands the verified artifact to the runtime.
/// </summary>
internal interface ICompiledAppAssignment
{
    Task<CompiledAppInstallation?> Current(string brain, string name, AppSnapshot snapshot, CancellationToken ct);
    Task<AppSnapshot> Assign(AuthorizedCompiledApp app, CancellationToken ct);
}
