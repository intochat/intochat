using Orleans.Metadata;

namespace DigitalBrain.Apps;

// Brings this brain's installed shipped apps to the catalog's version. Runs on every brain
// activation, so a code change is reflected in the running apps without a hand-posted update.
[Alias("apps.reconciler"), DefaultGrainType("apps.reconciler")]
public interface IShippedAppReconciler : INeuron
{
    Task Reconcile(CancellationToken ct = default);

    static string Key(string brainId) => $"{brainId}/apps-reconciler";
}