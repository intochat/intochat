using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// One per brain, keyed by the brain scope id: the keys of its connection neurons, nothing else.
[Alias("connection.index"), Orleans.Metadata.DefaultGrainType("connection.index")]
public interface IConnectionIndex : IGrainWithStringKey
{
    Task<string[]> List();
    Task Track(string connectionKey);
    Task Untrack(string connectionKey);
}
