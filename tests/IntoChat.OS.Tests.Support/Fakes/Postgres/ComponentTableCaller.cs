using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Postgres.Tables;

namespace IntoChat.OS.Tests.Support.Fakes.Postgres;

[DefaultNeuronType("test.component-table-caller")]
public interface IComponentTableCaller
{
    Task<string> DefineAndUpsert(string tableKey, string id, string title);
    Task<string?> ReadTitle(string tableKey, string id);
}

[Neuron("test.component-table-caller")]
public sealed class ComponentTableCallerNeuron : Neuron, IComponentTableCaller
{
    private static readonly TableDefinition Schema = new(
        [new("id", "text"), new("title", "text")],
        ["id"]);

    public async Task<string> DefineAndUpsert(string tableKey, string id, string title)
    {
        var table = Context.Get<ITable>(new NeuronId(Id.Brain, "postgres.table", tableKey));
        var accepted = await table.Define(Schema);
        await table.Upsert([new("id", id)], [new("title", title)]);
        return accepted.Table;
    }

    public async Task<string?> ReadTitle(string tableKey, string id)
    {
        var table = Context.Get<ITable>(new NeuronId(Id.Brain, "postgres.table", tableKey));
        var row = await table.Read([new("id", id)]);
        return row?.Single(v => v.Column == "title").Json;
    }
}

[Module("test-component-table-caller")]
public sealed class ComponentTableCallerModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}
