using Orleans.Metadata;

namespace IntoChat.OS.Modules.Apps;

// Brings this brain's installed features to the features folder's version. Runs on every brain
// activation, so a code change is reflected in the running apps without a hand-posted update.
[Alias("apps.reconciler"), DefaultGrainType("apps.reconciler")]
public interface IFeatureReconciler : INeuron
{
    Task Reconcile(CancellationToken ct = default);

    static string Key(string brainId) => $"{brainId}/apps-reconciler";
}
