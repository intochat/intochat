using DigitalBrain;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Orleans.Runtime;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;

namespace DigitalBrain.Postgres;

[GenerateSerializer]
internal sealed record PostgresTableState
{
    [Id(0)] public string? Owner { get; init; }
    [Id(1)] public AcceptedTable? Accepted { get; init; }
    [Id(2)] public string? Origin { get; init; }
    [Id(3)] public TableDefinition? Pending { get; init; }
    [Id(4)] public string? PhysicalTable { get; init; }
}

[GrainType("postgres.table")]
internal sealed partial class PostgresTableNeuron(IPostgresTableProvider provider, DigitalBrain.Framework.Capacity.ICapacity capacity,
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<PostgresTableState> state) : Neuron, ITable, ITableLifetime
{
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

    private ITableIndex Tables(string owner) => GrainFactory.GetGrain<ITableIndex>(owner);

    public async Task Retire(string owner)
    {
        if (state.State.Owner != owner) { return; }
        var table = state.State.Accepted?.Table ?? state.State.PhysicalTable ?? PostgresTablePolicy.PhysicalName(owner, this.GetPrimaryKeyString());
        await provider.DropAsync(PostgresCapacityKind.OriginOrPlatform(state.State.Origin), table, CancellationToken.None);
        await Persist(new());
    }

    private async Task Persist(PostgresTableState next)
    {
        var previous = state.State;
        state.State = next;
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
    }

    public async Task<AcceptedTable> Define(TableDefinition definition)
    {
        var scope = Scope();
        var normalized = PostgresTablePolicy.Validate(definition);
        await Tables(scope).RequireOpen();
        if (state.State.Accepted is { } accepted)
        {
            if (!PostgresTablePolicy.Compatible(accepted.Definition, normalized))
            { throw new PostgresQueryException("Incompatible table redefinition; migrations are not supported."); }
            await Tables(scope).Register(this.GetPrimaryKeyString());
            return accepted;
        }
        var table = state.State.PhysicalTable ?? PostgresTablePolicy.PhysicalName(scope, this.GetPrimaryKeyString());
        // The origin is pinned at first Define; later-registered capacity never migrates a table.
        if (state.State.Pending is { } pending && !PostgresTablePolicy.Compatible(pending, normalized))
        { throw new PostgresQueryException("Incompatible table redefinition; migrations are not supported."); }
        var origin = state.State.Origin
            ?? (await capacity.Resolve(PostgresCapacityKind.Kind, new(BrainScope.CurrentId(), CallerContextStamper.Require().AppId))).Origin;
        await Tables(scope).Register(this.GetPrimaryKeyString());
        await Persist(state.State with { Owner = scope, Origin = origin, Pending = normalized });
        await provider.DefineAsync(origin, table, normalized, CancellationToken.None);
        var result = new AcceptedTable(table, normalized, 1);
        await Persist(new() { Owner = scope, Accepted = result, Origin = origin });
        await PublishAsync(new TableDefined(table, result.Revision));
        return result;
    }

    public async Task<bool> Upsert(TableValue[] key, TableValue[] values)
    {
        var current = await Current();
        key = PostgresTablePolicy.Values(current.Definition, key, true);
        values = PostgresTablePolicy.Values(current.Definition, values, false);
        var changed = await provider.UpsertAsync(PostgresCapacityKind.OriginOrPlatform(state.State.Origin), current.Table, current.Definition, key, values, CancellationToken.None);
        if (changed) { await PublishAsync(new RowUpserted(current.Table, key)); }
        return changed;
    }

    public async Task<bool> Delete(TableValue[] key)
    {
        var current = await Current();
        key = PostgresTablePolicy.Values(current.Definition, key, true);
        var changed = await provider.DeleteAsync(PostgresCapacityKind.OriginOrPlatform(state.State.Origin), current.Table, current.Definition, key, CancellationToken.None);
        if (changed) { await PublishAsync(new RowDeleted(current.Table, key)); }
        return changed;
    }

    public async Task<TableValue[]?> Read(TableValue[] key)
    {
        var current = await Current();
        key = PostgresTablePolicy.Values(current.Definition, key, true);
        return await provider.ReadAsync(PostgresCapacityKind.OriginOrPlatform(state.State.Origin), current.Table, current.Definition, key, CancellationToken.None);
    }

    public async Task<TableValue[][]> Page(int offset = 0, int limit = 200)
    {
        var current = await Current();
        if (offset < 0 || limit is < 1 or > PostgresQuery.MaxRowsLimit)
        { throw new PostgresQueryException($"Offset must be nonnegative and limit must be 1–{PostgresQuery.MaxRowsLimit}."); }
        return await provider.PageAsync(PostgresCapacityKind.OriginOrPlatform(state.State.Origin), current.Table, current.Definition, offset, limit, CancellationToken.None);
    }
}
