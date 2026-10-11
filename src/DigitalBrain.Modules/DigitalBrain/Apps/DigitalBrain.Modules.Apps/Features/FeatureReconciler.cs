using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Brings this brain's unedited features to the features folder's version through the loader, the
// same path AppInstaller.Update takes; a copy the person edited, or that came from a person's
// brain, is untouched. One app failing must not keep the others from catching up.
// A circle brain is not reconciled (R125): it carries no OS default.
[GrainType("apps.reconciler")]
internal sealed class FeatureReconciler(
    IGrainFactory grains, IKernel kernel, FeatureCatalog catalog, FeatureLoader loader, ILogger<FeatureReconciler> logger,
    DigitalBrain.Framework.Platform.Identity.IIdentityClient identity)
    : Neuron, IFeatureReconciler
{
    public async Task Reconcile(CancellationToken ct = default)
    {
        var key = this.GetPrimaryKeyString();
        var brainId = key[..key.LastIndexOf('/')];
        if (await identity.KindOf(brainId).WaitAsync(ct) == DigitalBrain.Framework.Platform.Identity.BrainKind.Circle) { return; }
        // Reconciling runs on the brain's activation, with no caller: it names its own brain.
        List<string> names = [];
        await foreach (var instance in kernel.InstancesIn(brainId, kernel.TypeOf(typeof(IApp)), ct)) { if (IApp.NameOf(brainId, instance.Key) is { } app) { names.Add(app); } }
        foreach (var name in names)
        {
            AppSnapshot snapshot;
            try { snapshot = await grains.GetGrain<IApp>(IApp.Key(brainId, name)).Read().WaitAsync(ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Could not read {Brain}/{App} to reconcile it.", brainId, name); continue; }
            if (snapshot.Origin is not { Source: AppSource.Shipped } origin || snapshot.Edited) { continue; }
            AppBundle? bundle;
            try { bundle = await catalog.Find(origin.Publisher, origin.Name); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "The features folder could not serve {Owner}/{Name}.", origin.Publisher, origin.Name); continue; }
            if (bundle is null)
            { logger.LogWarning("{Brain}/{App} came from {Owner}/{Name}, which the features folder does not carry; it stays at {Version}.", brainId, name, origin.Publisher, origin.Name, origin.Version); continue; }
            if (AppVersions.Compare(bundle.Version, origin.Version) <= 0) { continue; }
            try { await loader.Load(brainId, name, bundle, origin, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Reconciling {Brain}/{App} to {Version} failed.", brainId, name, bundle.Version); continue; }
            await PublishAsync(new AppReconciled(name, origin.Version, bundle.Version));
        }
    }
}
