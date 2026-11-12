using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.State;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Postgres.Tables;

[GenerateSerializer, Alias("postgres.table-index-state")]
[ExportableState]
internal sealed record TableIndexState
{
    [Id(0)] public string[] Tables { get; init; } = [];
    [Id(1)] public bool Retired { get; init; }
}

// A plain neuron (06-08). Its state saves without a signal: which tables exist is no one's to watch.
[DigitalBrain.Neurons.Neuron(GrainType)]
internal sealed class TableIndexNeuron : DigitalBrain.Neurons.Neuron<TableIndexState>, ITableIndex
{
    public const string GrainType = "postgres.tables";

    private readonly SemaphoreSlim writes = new(1, 1);

    private string OwnerKey => $"{Id.Brain.Value}/{Id.Key}";

    public Task<string[]> ReadTables() => Task.FromResult(State.Tables.ToArray());

    public Task RequireOpen()
    {
        if (State.Retired) { throw new InvalidOperationException("This app's storage has been retired."); }
        return Task.CompletedTask;
    }

    public async Task Register(string tableId)
    {
        await writes.WaitAsync();
        try
        {
            await RequireOpen();
            if (State.Tables.Contains(tableId, StringComparer.Ordinal)) { return; }
            await Persist(State with { Tables = [.. State.Tables, tableId] });
        }
        finally { writes.Release(); }
    }

    public async Task Retire()
    {
        await writes.WaitAsync();
        try { if (!State.Retired) { await Persist(State with { Retired = true }); } }
        finally { writes.Release(); }
        // Registration must still answer while a table is finishing its in-flight Define.
        // The write gate is released before calls back to tables, so closed scopes refuse promptly.
        // Each call leaves as this neuron, so TableLifecycleGuard sees the index as the caller.
        foreach (var table in State.Tables)
        { await Context.Get<ITableLifetime>(SiloNeurons.IdOf(typeof(ITableLifetime), table)).Retire(OwnerKey); }
    }

    // A fresh install of the same app: its old tables were dropped by Retire, so it starts empty.
    public async Task Reopen()
    {
        await writes.WaitAsync();
        try { if (State.Retired) { await Persist(new()); } }
        finally { writes.Release(); }
    }

    protected override Task OnDeactivate(DigitalBrain.Neurons.DeactivationCause cause, CancellationToken cancellationToken)
    {
        writes.Dispose();
        return Task.CompletedTask;
    }

    private Task Persist(TableIndexState next) => Store(next);
}
