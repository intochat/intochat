namespace IntoChat.OS.Modules.Apps.AppRuns;

internal sealed record DeployedAppsState(DeployedApp[] Apps)
{
    public DeployedAppsState() : this([]) { }
}

// The brain's deployed sandbox apps (key deployed/index), written by each app's own neuron when it is deployed or removed.
[DigitalBrain.Neurons.Neuron("app.deployed-index")]
internal sealed class DeployedAppsNeuron : DigitalBrain.Neurons.Neuron<DeployedAppsState>, IDeployedApps
{
    public Task Put(DeployedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Store(new DeployedAppsState([.. State.Apps.Where(item => item.App != app.App), app]));
    }

    public Task Remove(string app) => State.Apps.Any(item => item.App == app)
        ? Store(new DeployedAppsState([.. State.Apps.Where(item => item.App != app)]))
        : Task.CompletedTask;

    public Task<DeployedApp[]> List() => Task.FromResult<DeployedApp[]>([.. State.Apps.OrderBy(item => item.App, StringComparer.Ordinal)]);
}
