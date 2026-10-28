using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// Runs a host-shipped built-in as its prebuilt package. The bytes are the host's own catalog's, never the brain's: a copy's recorded
// PackageDigest only names which shipped package it runs, and is honoured when the catalog still carries exactly that package for the
// copy's publisher and name. A copy the person edited, or one whose package the host no longer ships, keeps the source path.
internal sealed class ShippedPrograms(IServiceProvider plain, FeatureCatalog catalog)
{
    // The digest of the package the host ships for this publisher and name; null when it ships none, or ships the app as source.
    internal async Task<string?> DigestOf(string publisher, string name, CancellationToken ct = default)
        => await catalog.FindCompiled(publisher, name, ct) is { } shipped && CompiledPackageMetadata.IsProgram(shipped.Package.Descriptor) ? shipped.Package.Digest : null;

    // True when the prebuilt package now runs as the app's program; false when the copy is not one that runs a shipped package.
    internal async Task<bool> Assign(string appKey, AppOrigin? origin, bool edited, Dictionary<string, string> settings, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(settings);
        if (edited || origin is not { Source: AppSource.Shipped, PackageDigest: { Length: > 0 } digest }) { return false; }
        if (await catalog.FindCompiled(origin.Publisher, origin.Name, ct) is not { } shipped
            || !CompiledPackageMetadata.IsProgram(shipped.Package.Descriptor) || shipped.Package.Digest != digest)
        { return false; }
        var behaviors = await SandboxCompiledApps.Behaviors(shipped.Package, ct);
        var programs = SiloNeurons.Get<IBrainPrograms>(plain, BrainRuntimeKeys.For(BrainKey.BrainOf(appKey)));
        var configuration = new Dictionary<string, string>(settings, StringComparer.Ordinal) { ["Digest"] = digest };
        await PrebuiltPrograms.Assign(programs, appKey, behaviors, configuration, digest, ct);
        return true;
    }
}
