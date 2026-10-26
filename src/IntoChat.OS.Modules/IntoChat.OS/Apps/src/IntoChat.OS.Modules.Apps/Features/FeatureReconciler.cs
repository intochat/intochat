using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// Brings this brain's unedited features to the features folder's version through the loader, the
// same path AppInstaller.Update takes; a copy the person edited, or that came from a person's
// brain, is untouched. One app failing must not keep the others from catching up.
// It only updates what a brain already has, so a circle brain (R125: born with its room, no OS
// default) is reconciled like any other.
// A plain neuron (06-08), reading the brain's plain apps.
[DigitalBrain.Neuron("apps.reconciler")]
internal sealed class FeatureReconciler(
    IKernel kernel, FeatureCatalog catalog, FeatureLoader loader, ILogger<FeatureReconciler> logger,
    IEnumerable<IFeatureMigration> migrations)
    : DigitalBrain.Neuron, IFeatureReconciler
{
    public async Task Reconcile(CancellationToken ct = default)
    {
        var brainId = Id.Brain.Value;
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
            if (snapshot.MigrationTarget is not null) { continue; }
            if (snapshot.Origin is not { Source: AppSource.Shipped } origin) { continue; }
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
}
