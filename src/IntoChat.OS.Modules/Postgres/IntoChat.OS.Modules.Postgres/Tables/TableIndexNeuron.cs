using IntoChat.OS.Framework;

namespace IntoChat.OS.Postgres.Tables;

[GenerateSerializer, Alias("postgres.table-index-state")]
internal sealed record TableIndexState
{
    [Id(0)] public string[] Tables { get; init; } = [];
    [Id(1)] public bool Retired { get; init; }
}

[GrainType(GrainType)]
internal sealed class TableIndexNeuron(
    [PersistentState("tables", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TableIndexState> state)
    : Grain, ITableIndex
{
    public const string GrainType = "postgres.tables";

    private readonly SemaphoreSlim writes = new(1, 1);

    public Task<string[]> ReadTables() => Task.FromResult(state.State.Tables.ToArray());

    public Task RequireOpen()
    {
        if (state.State.Retired) { throw new InvalidOperationException("This app's storage has been retired."); }
        return Task.CompletedTask;
    }

    public async Task Register(string tableId)
    {
        await writes.WaitAsync();
        try
        {
            await RequireOpen();
            if (state.State.Tables.Contains(tableId, StringComparer.Ordinal)) { return; }
            await Persist(state.State with { Tables = [.. state.State.Tables, tableId] });
        }
        finally { writes.Release(); }
    }

    public async Task Retire()
    {
        await writes.WaitAsync();
        try { if (!state.State.Retired) { await Persist(state.State with { Retired = true }); } }
        finally { writes.Release(); }
        // Registration must still answer while a table is finishing its in-flight Define.
        // The write gate is released before calls back to tables, so closed scopes refuse promptly.
        foreach (var table in state.State.Tables)
        { await GrainFactory.GetGrain<ITableLifetime>(table).Retire(this.GetPrimaryKeyString()); }
    }

    // A fresh install of the same app: its old tables were dropped by Retire, so it starts empty.
    public async Task Reopen()
    {
        await writes.WaitAsync();
        try { if (state.State.Retired) { await Persist(new()); } }
        finally { writes.Release(); }
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        writes.Dispose();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    private async Task Persist(TableIndexState next)
    {
        var previous = state.State;
        state.State = next;
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
    }
}
