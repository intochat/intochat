namespace IntoChat.OS.Modules.Apps;

// Brings this brain's installed features to the shipped catalog's version. Runs on every brain
// activation, so a code change is reflected in the running apps without a hand-posted update.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("apps.reconciler"), DigitalBrain.Contracts.DefaultNeuronType("apps.reconciler")]
public interface IFeatureReconciler
{
    Task Reconcile(CancellationToken ct = default);

    static string Key(string brainId) => $"{brainId}/apps-reconciler";
}
