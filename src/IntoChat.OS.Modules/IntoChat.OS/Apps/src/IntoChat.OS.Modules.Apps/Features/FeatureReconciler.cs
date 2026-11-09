using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// Installs every app in the shipped catalog (and each ShippedApps root) that this brain does
// not already have, then brings unedited shipped copies to the catalog's version through the
// loader, the same path AppInstaller.Update takes. A copy the person edited, or that came from
// a person's brain, is untouched. One app failing must not keep the others from catching up.
// A circle brain (R125) is born with its room only, so the install is skipped; updates still run.
// A plain neuron (06-08), reading the brain's plain apps.
[DigitalBrain.Neuron("apps.reconciler")]
internal sealed class FeatureReconciler(
    IKernel kernel, FeatureCatalog catalog, FeatureLoader loader, ILogger<FeatureReconciler> logger,
    IEnumerable<IFeatureMigration> migrations, ICapabilityBroker? directory = null,
    IntoChat.OS.Connections.Platform.Identity.IIdentityClient? identity = null)
    : DigitalBrain.Neuron, IFeatureReconciler
{
    public async Task Reconcile(CancellationToken ct = default)
    {
        var brainId = Id.Brain.Value;
        // A circle is born with its room only (R125). Every other brain receives the catalog's apps.
        if (directory is null || await (await directory.Identity(ct)).KindOf(brainId, ct) != BrainKind.Circle)
        {
            await ShippedInstall.Missing(catalog, loader, brainId, async name =>
                await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/" + name)).Read().WaitAsync(ct),
                (error, app) => logger.LogWarning(error, "Default app {App} did not install in brain {Brain}.", app, brainId), ct);
        }
        foreach (var migration in migrations)
        {
            try { await migration.PrepareBrain(brainId, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Preparing app migration for {Brain} failed; unrelated features can still reconcile.", brainId); }
        }
        // Reconciling runs on the brain's activation, with no caller: it names its own brain.
        List<string> names = [];
        await foreach (var instance in kernel.InstancesIn(brainId, kernel.TypeOf(typeof(IApp)), ct)) { if (IApp.NameOf(brainId, instance.Key) is { } app) { names.Add(app); } }
        foreach (var name in names)
        {
            AppSnapshot snapshot;
            try { snapshot = await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/" + name)).Read().WaitAsync(ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Could not read {Brain}/{App} to reconcile it.", brainId, name); continue; }
            if (snapshot.Origin is not { Source: AppSource.Shipped } origin) { continue; }
            await GrantShipped(brainId, name, origin);
            var migrating = false;
            try
            {
                foreach (var migration in migrations) { migrating |= await migration.Required(brainId, name, snapshot, ct); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Could not check migration of {Brain}/{App}.", brainId, name); continue; }
            if (snapshot.Edited && !migrating) { continue; }
            AppBundle? bundle;
            try { bundle = await catalog.Find(origin.Publisher, origin.Name); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "The features folder could not serve {Owner}/{Name}.", origin.Publisher, origin.Name); continue; }
            if (bundle is null)
            { logger.LogWarning("{Brain}/{App} came from {Owner}/{Name}, which the features folder does not carry; it stays at {Version}.", brainId, name, origin.Publisher, origin.Name, origin.Version); continue; }
            if (!migrating && AppVersions.Compare(bundle.Version, origin.Version) <= 0 && !AppInstaller.Unconfirmed(snapshot.Status)) { continue; }
            // Activation never widens: a release asking for more than the copy's consent waits for the person in Apps.
            if (!AppConsent.IsEmpty(AppConsent.Added(origin.Consented ?? snapshot.Manifest.Requires, bundle.Manifest.Requires)))
            {
                logger.LogInformation("{Brain}/{App} {Version} asks for more than was accepted; it stays at {Installed} until the person consents.",
                    brainId, name, bundle.Version, origin.Version);
                continue;
            }
            try { await loader.Load(brainId, name, bundle, origin, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Reconciling {Brain}/{App} to {Version} failed.", brainId, name, bundle.Version); continue; }
            await Publish(new AppReconciled(name, origin.Version, bundle.Version));
        }
    }

    // Every activation brings a shipped shell app's grants up to what it needs; recording is idempotent.
    private async Task GrantShipped(string brainId, string name, AppOrigin origin)
    {
        if (identity is null || origin.Publisher != ShippedGrants.Publisher || ShippedGrants.For(brainId, name) is not { Length: > 0 } grants) { return; }
        try { await identity.GrantShipped(brainId, grants); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Granting shipped {Brain}/{App} what it needs failed.", brainId, name); }
    }
}
