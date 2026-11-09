namespace IntoChat.OS.Modules.Apps;

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
