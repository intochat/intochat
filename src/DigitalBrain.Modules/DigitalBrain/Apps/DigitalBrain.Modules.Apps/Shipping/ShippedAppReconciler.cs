using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Brings this brain's unedited shipped apps to the catalog's version. Replace then RecordOrigin is
// the same path AppInstaller.Update takes; a copy the person edited, or that came from a person's
// brain, is untouched. One app failing must not keep the others from catching up.
[GrainType("apps.reconciler")]
internal sealed class ShippedAppReconciler(
    IGrainFactory grains, IKernel kernel, ILogger<ShippedAppReconciler> logger, ShippedCatalog? catalog = null)
    : Neuron, IShippedAppReconciler
{
    public async Task Reconcile(CancellationToken ct = default)
    {
        var key = this.GetPrimaryKeyString();
        var brainId = key[..key.LastIndexOf('/')];
        // Reconciling runs on the brain's activation, with no caller: it names its own brain.
        List<string> names = [];
        await foreach (var instance in kernel.InstancesIn(brainId, kernel.TypeOf(typeof(IApp)), ct)) { if (IApp.NameOf(brainId, instance.Key) is { } app) { names.Add(app); } }
        foreach (var name in names)
        {
            var app = grains.GetGrain<IApp>(IApp.Key(brainId, name));
            AppSnapshot snapshot;
            try { snapshot = await app.Read().WaitAsync(ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Could not read {Brain}/{App} to reconcile it.", brainId, name); continue; }
            if (snapshot.Origin is not { Source: AppSource.Shipped } origin || snapshot.Edited) { continue; }
            if (catalog is null) { continue; }
            AppBundle? bundle;
            try { bundle = await catalog.Find(origin.Publisher, origin.Name); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "The catalog could not serve {Publisher}/{Name}.", origin.Publisher, origin.Name); continue; }
            if (bundle is null || AppVersions.Compare(bundle.Version, origin.Version) <= 0) { continue; }
            try
            {
                await app.Replace(bundle with { Name = name }).WaitAsync(ct);
                await app.RecordOrigin(origin with { Version = bundle.Version }).WaitAsync(ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Reconciling {Brain}/{App} to {Version} failed.", brainId, name, bundle.Version); continue; }
            await PublishAsync(new AppReconciled(name, origin.Version, bundle.Version));
        }
    }
}