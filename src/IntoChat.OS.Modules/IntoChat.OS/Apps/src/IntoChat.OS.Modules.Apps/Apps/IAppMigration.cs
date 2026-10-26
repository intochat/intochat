namespace IntoChat.OS.Modules.Apps;

// Host infrastructure only: deliberately not a contract of the Apps contracts assembly, so no script or HTTP edge names it.
// A plain contract of the app neuron (06-08), alias unchanged.
[DigitalBrain.Contracts.NeuronAlias("apps.migration"), DigitalBrain.Contracts.DefaultNeuronType(IntoChat.OS.Connections.AppGrainType.Name)]
public interface IAppMigration
{
    Task Pause(string migration);
    Task<AppState> Archive(string migration);
    Task Adopt(string migration, string source);
    Task Redirect(string migration, string target);
    Task Resume(string migration);
    Task Accept(string operation, string input, Guid requestId);
    Task MergeRequests(RetainedRequest[] requests);
    // The requests this app retains for synapses that start later (06-08: the plain app keeps them in its state).
    [DigitalBrain.ReadOnlyCall] Task<RetainedRequest[]> RetainedRequests();
}

// A host can gate a feature replacement on adoption of its existing data. A failed before/after
// call is retried by the ordinary feature reconciler; implementations must be resumable.
public interface IFeatureMigration
{
    Task PrepareBrain(string brain, CancellationToken ct);
    Task<bool> Required(string brain, string name, AppSnapshot current, CancellationToken ct);
    Task<AppBundle> BeforeLoad(string brain, string name, AppBundle bundle, CancellationToken ct);
    Task AfterLoad(string brain, string name, AppBundle bundle, CancellationToken ct);
}

// One request the app keeps for a synapse that starts later, numbered in the app's own request order (06-08: replaces the legacy
// RetainedSignal, whose payload was an os Signal).
[GenerateSerializer, Alias("apps.retained-request")]
public sealed record RetainedRequest([property: Id(0)] long Sequence, [property: Id(1)] Signals.AppRequested Request);
