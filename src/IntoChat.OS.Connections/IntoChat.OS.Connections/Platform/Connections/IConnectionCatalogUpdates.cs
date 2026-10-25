using Orleans.Concurrency;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// Internal notification port on the public catalog neuron. One-way avoids a cycle when a
// catalog read is already awaiting the connection that has just changed.
[Alias("integrations.catalog-updates"), Orleans.Metadata.DefaultGrainType("integrations.accounts")]
public interface IConnectionCatalogUpdates : IGrainWithStringKey
{
    [OneWay] Task Changed(string connectionKey);
}
