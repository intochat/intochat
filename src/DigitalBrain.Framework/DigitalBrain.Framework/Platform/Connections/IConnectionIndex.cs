namespace DigitalBrain.Framework.Platform.Connections;

// One per brain, keyed by the brain scope id: the keys of its connection neurons, nothing else.
[PlatformOnly, Alias("connection.index"), Orleans.Metadata.DefaultGrainType("connection.index")]
public interface IConnectionIndex : INeuron
{
    Task<string[]> List();
    Task Track(string connectionKey);
    Task Untrack(string connectionKey);
}
