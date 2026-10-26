using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;

namespace IntoChat.OS.Modules.Postgres.Tables;

// One index per TableOwner: which tables the app defined, and whether its storage is retired.
[Alias("postgres.table-index")]
public interface ITableIndex : IGrainWithStringKey
{
    [Orleans.Concurrency.ReadOnly] Task<string[]> ReadTables();
    [Orleans.Concurrency.AlwaysInterleave] Task Register(string tableId);
    [Orleans.Concurrency.AlwaysInterleave] Task RequireOpen();
    Task Retire();
    Task Reopen();
}

// Implemented by the plain TableNeuron (06-05b); TableLifecycleGuard admits only the owner's index as its caller.
[DigitalBrain.Contracts.NeuronAlias("postgres.table-lifetime"), DigitalBrain.Contracts.DefaultNeuronType(TableNeuron.GrainType)]
public interface ITableLifetime
{
    Task Retire(string owner);
}
