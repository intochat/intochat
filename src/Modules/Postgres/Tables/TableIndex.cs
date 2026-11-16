
namespace IntoChat.OS.Modules.Postgres.Tables;

// One index per TableOwner: which tables the app defined, and whether its storage is retired.
// A plain neuron (06-08), keyed by the owner "{brain}/tables/{app}"; TableLifecycleGuard admits its callers.
[DigitalBrain.Contracts.NeuronAlias("postgres.table-index"), DigitalBrain.Contracts.DefaultNeuronType(TableIndexNeuron.GrainType)]
internal interface ITableIndex
{
    [DigitalBrain.Contracts.ReadOnlyCall] Task<string[]> ReadTables();
    [DigitalBrain.Contracts.Interleaves] Task Register(string tableId);
    [DigitalBrain.Contracts.Interleaves] Task RequireOpen();
    Task Retire();
    Task Reopen();
}

// Implemented by the plain TableNeuron (06-05b); TableLifecycleGuard admits only the owner's index as its caller.
[DigitalBrain.Contracts.NeuronAlias("postgres.table-lifetime"), DigitalBrain.Contracts.DefaultNeuronType(TableNeuron.GrainType)]
internal interface ITableLifetime
{
    [DigitalBrain.Contracts.OneWayCall]
    Task Retire(string owner);
}
