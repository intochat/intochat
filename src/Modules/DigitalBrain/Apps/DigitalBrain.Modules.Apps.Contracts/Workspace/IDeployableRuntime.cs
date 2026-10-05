namespace DigitalBrain.Apps;

// A runtime that materializes an app's programs as running neurons. The app grain stays
// runtime-agnostic: it hands over programs plus configuration and stores the keys the
// runtime returns; retiring hands those same keys back. A runtime with nothing to deploy
// (group chat, prompt) simply never registers one of these.
public interface IDeployableRuntime
{
    string Name { get; }
    Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken);
    Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken);
}

[GenerateSerializer, Alias("apps.runtime-deployment")]
public sealed record RuntimeDeployment(
    [property: Id(0)] string AppKey,
    [property: Id(1)] string BrainId,
    [property: Id(2)] int Generation,
    [property: Id(3)] Dictionary<string, string> Programs,
    [property: Id(4)] Dictionary<string, string> Configuration);
