using DigitalBrain.Framework;
using DigitalBrain.Postgres.Tables.Signals;

namespace DigitalBrain.Postgres.Tables;

[GenerateSerializer]
internal sealed record TableState
{
    [Id(0)] public string? Owner { get; init; }
    [Id(1)] public AcceptedTable? Accepted { get; init; }
    [Id(2)] public TablePlacement? Placement { get; init; }
    [Id(3)] public TableDefinition? Pending { get; init; }
    [Id(4)] public string? PhysicalTable { get; init; }
}

[GrainType(GrainType)]
internal sealed partial class TableNeuron(ITableStore provider, DigitalBrain.Framework.Capacity.ICapacity capacity,
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<TableState> state) : Neuron, ITable, ITableLifetime
{
    public const string GrainType = "postgres.table";

    public override NeuronAccess Access(string operation)
    {
        if (operation is nameof(Define) or nameof(Upsert) or nameof(Delete) or nameof(Page) or nameof(Read) or nameof(Watch) or nameof(Unwatch))
        {
            _ = Scope();
            return new(BrainScope.CurrentId());
        }
        return base.Access(operation);
    }

    private string Scope()
    {
        var scope = TableOwner.Current().Key;
        if (state.State.Owner is { } owner && owner != scope)
        { throw new UnauthorizedAccessException("This table belongs to another brain or app."); }
        return scope;
    }

    private async Task<AcceptedTable> Current()
    {
        // Check each operation: caching would allow writes after the owner closes but before
        // its queued Retire reaches this table. Table serialization then orders accepted work before DROP.
        await Tables(Scope()).RequireOpen();
        return state.State.Accepted ?? throw new PostgresQueryException("Define the table first.");
    }

    // Retire can reach a table whose Define never persisted; there is nothing placed elsewhere to reclaim.
    private TablePlacement Pinned => state.State.Placement ?? TablePlacement.Shared;

    private static TablePlacement Placement(string origin)
        => origin.StartsWith("db:", StringComparison.Ordinal) ? TablePlacement.BrainDatabase(origin[3..]) : TablePlacement.Shared;

    private ITableIndex Tables(string owner) => GrainFactory.GetGrain<ITableIndex>(owner);

    public async Task Retire(string owner)
    {
        if (state.State.Owner != owner) { return; }
        var table = state.State.Accepted?.Table ?? state.State.PhysicalTable ?? TablePolicy.PhysicalName(owner, this.GetPrimaryKeyString());
        await provider.DropAsync(Pinned, table, CancellationToken.None);
        await Persist(new());
    }

    private async Task Persist(TableState next)
    {
        var previous = state.State;
        state.State = next;
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
    }

    public async Task<AcceptedTable> Define(TableDefinition definition)
    {
        var scope = Scope();
        var normalized = TablePolicy.Validate(definition);
        await Tables(scope).RequireOpen();
        if (state.State.Accepted is { } accepted)
        {
            if (!TablePolicy.Compatible(accepted.Definition, normalized))
            { throw new PostgresQueryException("Incompatible table redefinition; migrations are not supported."); }
            await Tables(scope).Register(this.GetPrimaryKeyString());
            return accepted;
        }
        var table = state.State.PhysicalTable ?? TablePolicy.PhysicalName(scope, this.GetPrimaryKeyString());
        // The placement is pinned at first Define; a later policy or binding never moves a table.
        if (state.State.Pending is { } pending && !TablePolicy.Compatible(pending, normalized))
        { throw new PostgresQueryException("Incompatible table redefinition; migrations are not supported."); }
        var placement = state.State.Placement
            ?? Placement((await capacity.Resolve("postgres", new(BrainScope.CurrentId(), CallerContextStamper.Require().AppId))).Origin);
        await Tables(scope).Register(this.GetPrimaryKeyString());
        await Persist(state.State with { Owner = scope, Placement = placement, Pending = normalized });
        await provider.DefineAsync(placement, table, normalized, CancellationToken.None);
        var result = new AcceptedTable(table, normalized, 1);
        await Persist(new() { Owner = scope, Accepted = result, Placement = placement });
        await PublishAsync(new TableDefined(table, result.Revision));
        return result;
    }

    public async Task<bool> Upsert(TableValue[] key, TableValue[] values)
    {
        var current = await Current();
        key = TablePolicy.Values(current.Definition, key, true);
        values = TablePolicy.Values(current.Definition, values, false);
        var changed = await provider.UpsertAsync(Pinned, current.Table, current.Definition, key, values, CancellationToken.None);
        if (changed) { await PublishAsync(new RowUpserted(current.Table, key)); }
        return changed;
    }

    public async Task<bool> Delete(TableValue[] key)
    {
        var current = await Current();
        key = TablePolicy.Values(current.Definition, key, true);
        var changed = await provider.DeleteAsync(Pinned, current.Table, current.Definition, key, CancellationToken.None);
        if (changed) { await PublishAsync(new RowDeleted(current.Table, key)); }
        return changed;
    }

    public async Task<TableValue[]?> Read(TableValue[] key)
    {
        var current = await Current();
        key = TablePolicy.Values(current.Definition, key, true);
        return await provider.ReadAsync(Pinned, current.Table, current.Definition, key, CancellationToken.None);
    }

    public async Task<TableValue[][]> Page(int offset = 0, int limit = 200)
    {
        var current = await Current();
        if (offset < 0 || limit is < 1 or > PostgresQuery.MaxRowsLimit)
        { throw new PostgresQueryException($"Offset must be nonnegative and limit must be 1–{PostgresQuery.MaxRowsLimit}."); }
        return await provider.PageAsync(Pinned, current.Table, current.Definition, offset, limit, CancellationToken.None);
    }
}
