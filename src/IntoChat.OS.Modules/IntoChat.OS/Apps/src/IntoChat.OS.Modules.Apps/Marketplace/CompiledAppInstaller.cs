using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

internal sealed record AuthorizedCompiledApp(ValidatedAppPackage Package, AppManifest Manifest, CompiledAppTrustReceipt Receipt);

internal sealed class CompiledAppInstaller(IntoChat.OS.Runtime.IPlainNeurons plain, AppInstaller policy,
    CompiledAppPackages packages, CompiledAppReviewStore reviews, TimeProvider clock)
{
    // Inspection is inert: artifacts and metadata are read without initializing package code. Missing capabilities
    // are refused before even reading the destination owner. A review grants nothing and changes no app state.
    internal async Task<CompiledAppReview> Prepare(string digest, string name, CancellationToken ct)
    {
        Name(name);
        var caller = await RequireOwner(ct);
        var brain = caller.BrainId;
        var package = await packages.Read(digest, ct);
        var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
        await policy.CheckCapabilities(brain, manifest, ct);
        var snapshot = await plain.Get<IApp>(IApp.Key(brain, name)).Read().WaitAsync(ct);
        if (snapshot.MigrationTarget is not null) { throw new InvalidOperationException("Choose the app's current destination before installing a package."); }
        await CheckBindings(brain, manifest, snapshot.Connections, ct);
        return reviews.Create(brain, name, package.Digest, snapshot.ProgramRevision, snapshot.Manifest.Title.Length > 0,
            manifest.Requires ?? new(), [.. package.Descriptor.RequiredHostAdapters], caller);
    }

    internal async Task<CompiledAppReview> PrepareForOwner(string digest, string name, long revision, bool replacesExisting,
        IReadOnlyDictionary<string, string> connections, CancellationToken ct)
    {
        Name(name);
        var caller = await RequireOwner(ct);
        var package = await packages.Read(digest, ct);
        var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
        await policy.CheckCapabilities(caller.BrainId, manifest, ct);
        await CheckBindings(caller.BrainId, manifest, connections, ct);
        return reviews.Create(caller.BrainId, name, package.Digest, revision, replacesExisting,
            manifest.Requires ?? new(), [.. package.Descriptor.RequiredHostAdapters], caller);
    }

    // The owner supplies its current revision and bindings. Calling Read on that owner here would deadlock its turn.
    // Recheck current authority and connections: an earlier review is neither a grant nor proof of current ownership.
    internal async Task<AuthorizedCompiledApp> Authorize(CompiledAppApproval approval, string name, long revision,
        IReadOnlyDictionary<string, string> connections, CancellationToken ct)
    {
        Name(name);
        var caller = await RequireOwner(ct);
        var review = reviews.Authorize(approval, caller.BrainId, name, revision, caller);
        var package = await packages.Read(review.Digest, ct);
        var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
        await policy.CheckCapabilities(caller.BrainId, manifest, ct);
        await CheckBindings(caller.BrainId, manifest, connections, ct);
        return new(package, manifest, new(review.Digest, review.Brain, review.Name, review.ExpectedRevision, approval.OperationId,
            caller.AccountId, caller.PrincipalId, clock.GetUtcNow(), review.Requires, review.HostAdapters, review.ReplacesExisting));
    }

    // The committed owner calls this before admitting dispatch, and again on interrupted recovery. Authority grants
    // are idempotent. A revoked owner cannot silently grant new access during recovery; the generation stays blocked.
    internal async Task ApplyGrants(CompiledAppTrustReceipt receipt, CancellationToken ct)
    {
        var caller = new CallerContext
        {
            AccountId = receipt.AccountId, BrainId = receipt.Brain, PrincipalId = receipt.PrincipalId,
            Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
        };
        if (!await policy.IsOwner(receipt.Brain, caller).WaitAsync(ct)) { throw new UnauthorizedAccessException(AppInstaller.OwnerOnly); }
        await policy.Grant(IApp.Key(receipt.Brain, receipt.Name), receipt.Requires.SemanticTypes, caller, ct);
    }

    internal async Task<CallerContext> RequireOwner(CancellationToken ct)
    {
        var caller = CallerContextStamper.Require();
        if (caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp || CallerContextStamper.IsModelOriginated(caller)
            || !await policy.IsOwner(caller.BrainId, caller).WaitAsync(ct))
        { throw new UnauthorizedAccessException("Only the brain owner can approve and install trusted compiled code."); }
        return caller;
    }

    private async Task CheckBindings(string brain, AppManifest manifest, IReadOnlyDictionary<string, string> connections, CancellationToken ct)
    {
        if (await policy.Unbound(brain, manifest, new(connections, StringComparer.Ordinal), ct) is { } refusal)
        { throw new InvalidOperationException(refusal); }
    }

    private static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        { throw new ArgumentException("Choose an app name using lowercase letters, digits and dashes.", nameof(name)); }
    }
}
