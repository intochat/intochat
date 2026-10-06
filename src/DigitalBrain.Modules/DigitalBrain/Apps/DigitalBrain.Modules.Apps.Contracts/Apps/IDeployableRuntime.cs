namespace DigitalBrain.Apps;

// A runtime that materializes an app's programs as running neurons. The app grain stays
// runtime-agnostic: it hands over programs plus configuration and stores the keys the
// runtime returns; retiring hands those same keys back. A runtime with nothing to deploy
// (group chat, prompt) simply never registers one of these.
public interface IDeployableRuntime
{
    string Name { get; }
    // The storage keys this runtime will use for these paths — deterministic, so the app can
    // track them before a deploy and retire exactly the right ones after a delete.
    string[] Keys(string appKey, int generation, IReadOnlyList<string> paths);
    Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken);
    // An edit of already-running programs: restarts them but preserves their durable
    // subscriptions and buffered signals. Defaults to a full deploy for runtimes without one.
    Task<string[]> Swap(RuntimeDeployment deployment, CancellationToken cancellationToken) => Deploy(deployment, cancellationToken);
    Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken);
}

[GenerateSerializer, Alias("apps.runtime-deployment")]
public sealed record RuntimeDeployment(
    [property: Id(0)] string AppKey,
    [property: Id(1)] string BrainId,
    [property: Id(2)] int Generation,
    [property: Id(3)] Dictionary<string, string> Programs,
    [property: Id(4)] Dictionary<string, string> Configuration);
