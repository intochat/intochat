using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

// Loads the features folder at boot (the feature.load spans), then watches the brain's lifecycle the
// way OsDefaults installs on it: every activation (the kernel's Activated signal) brings the brain's
// unedited features to the folder's version. The pump is fire-and-forget from the startup task; a
// failure never fails the silo or a brain's activation.
internal sealed class FeatureBoot(
    IGrainFactory grains, FeatureCatalog catalog, LocalSignalHub hub, ILogger<FeatureBoot> logger) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken)
    {
        var activations = hub.Subscribe<Activated>();
        _ = Task.Run(() => PumpAsync(activations, cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task PumpAsync(LocalSignalHub.Subscription<Activated> activations, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await catalog.Bundles();
            logger.LogInformation("Loaded {Count} features from {Path}.", loaded.Count, catalog.Directory?.Path ?? "(none)");
            foreach (var failure in await catalog.Failures())
            { logger.LogError("Feature {Owner}/{Name} {Version} was skipped: {Reason}", failure.Owner, failure.Name, failure.Version, failure.Reason); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogError(error, "The features folder {Path} did not load.", catalog.Directory?.Path); }
        try
        {
            await foreach (var activated in activations.Reader.ReadAllAsync(cancellationToken))
            {
                // The brain neuron publishes Activated; its publisher name is "brain/{brainId}".
                var name = activated.Publisher.Name;
                var brainId = name[(name.IndexOf('/') + 1)..];
                try { await grains.GetGrain<IFeatureReconciler>(IFeatureReconciler.Key(brainId)).Reconcile(cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Reconciling features for brain {Brain} failed.", brainId); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogError(error, "The feature reconciler stopped watching activations."); }
    }
}
