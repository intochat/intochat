using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CompiledAppDevelopment(IntoChat.OS.Runtime.IPlainNeurons plain, CompiledAppInstaller installer,
    CompiledAppPackages packages, TimeProvider clock)
{
    private readonly CompiledAppDevelopmentLeases leases = new(clock);
    internal async Task<CompiledAppDevelopmentLease> Begin(string name, string project, Guid operationId, long expectedRevision,
        bool trustProject, CancellationToken ct)
    {
        if (!trustProject) { throw new UnauthorizedAccessException("Explicitly trust future builds of this project before starting development."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var owner = await installer.RequireOwner(ct);
        var snapshot = await plain.Get<IApp>(IApp.Key(owner.BrainId, name)).Read().WaitAsync(ct);
        var active = snapshot.ActiveCompiled ?? throw new InvalidOperationException("Install and approve the initial package first.");
        if (snapshot.MigrationTarget is not null || !active.DispatchReady || operationId == Guid.Empty || active.Receipt.Brain != owner.BrainId || active.Receipt.Name != name || active.OperationId != operationId || active.Revision != expectedRevision
            || snapshot.ProgramRevision != expectedRevision || active.Receipt.AccountId != owner.AccountId
            || active.Receipt.PrincipalId != owner.PrincipalId)
        { throw new UnauthorizedAccessException("The development lease must match the owner's current approved installation."); }
        var package = await packages.Read(active.Digest, ct);
        return leases.Create(name, project, package.Descriptor.Id, active.Receipt.Requires, active.Receipt.HostAdapters, owner);
    }

    internal async Task<CompiledAppDevelopmentLease> Renew(string token, string project, CancellationToken ct)
        => leases.Renew(token, project, await installer.RequireOwner(ct));

    internal async Task Revoke(string token, string project, CancellationToken ct)
        => leases.Revoke(token, project, await installer.RequireOwner(ct));
    internal async Task<AuthorizedCompiledApp> Authorize(CompiledAppReload request, string name, long revision,
        IReadOnlyDictionary<string, string> connections, CancellationToken ct)
    {
        var owner = await installer.RequireOwner(ct);
        var entry = leases.Require(request.LeaseToken, request.Project, owner);
        if (entry.Lease.Name != name || request.ExpectedRevision != revision || request.OperationId == Guid.Empty)
        { throw new InvalidOperationException("The installed app changed or the reload destination is invalid."); }
        var package = await packages.Read(request.Digest, ct);
        var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
        if (!entry.Allows(package.Descriptor.Id, manifest.Requires, [.. package.Descriptor.RequiredHostAdapters]))
        { throw new UnauthorizedAccessException("Requirements or host adapters changed; review this package and renew project trust."); }
        var review = await installer.PrepareForOwner(request.Digest, name, revision, true, connections, ct);
        var authorized = await installer.Authorize(new(review.Token, request.Digest, request.OperationId, true, AppConsent.Of(review.Requires), true), name, revision, connections, ct);
        // Recheck after asynchronous validation so revocation/expiry during inspection cannot authorize code.
        _ = leases.Require(request.LeaseToken, request.Project, owner);
        return authorized;
    }
}
