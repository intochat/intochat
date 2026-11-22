using System.Text.Json;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Postgres.Tables;

namespace IntoChat.OS.Tests.Support.Fakes.Postgres;

/// <summary>
/// In-memory <see cref="ITableStore"/> fake keyed by <c>(placement, physical table, primary key)</c>
/// so <see cref="TableNeuron"/> runs with real validation, ownership and signals without a PostgreSQL server.
/// </summary>
public sealed class FakeTableStore : ITableStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(TablePlacement Placement, string Table), TableEntry> _tables = [];
    private readonly Queue<Exception> _failures = new();

    /// <summary>Distinct physical table names currently defined in the fake store.</summary>
    public IReadOnlyList<string> Tables
    {
        get
        {
            lock (_gate)
            {
                return [.. _tables.Keys.Select(k => k.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Queues an exception to be thrown on the next store operation.</summary>
    public void FailNext(Exception? error = null)
    {
        lock (_gate)
        {
            _failures.Enqueue(error ?? new PostgresUnavailableException("Fake table store failure."));
        }
    }

    /// <summary>Returns all rows across defined tables whose physical name contains <paramref name="physicalTableContains"/>.</summary>
    public TableValue[][] Rows(string physicalTableContains)
    {
        ArgumentNullException.ThrowIfNull(physicalTableContains);
        lock (_gate)
        {
            return
            [
                .. _tables
                    .Where(pair => pair.Key.Table.Contains(physicalTableContains, StringComparison.Ordinal))
                    .OrderBy(pair => pair.Key.Table, StringComparer.Ordinal)
                    .SelectMany(pair => pair.Value.OrderedRows())
                    .Select(CloneRow),
            ];
        }
    }

    /// <summary>Returns all rows in the table owned by <paramref name="ownerComponent"/> in <paramref name="brain"/> with logical key <paramref name="tableKey"/>.</summary>
    public TableValue[][] RowsFor(DigitalBrain.BrainId brain, string ownerComponent, string tableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerComponent);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableKey);
        var owner = new TableOwner(brain.Value, ownerComponent);
        var physical = TablePolicy.PhysicalName(owner.Key, $"{brain.Value}/{tableKey}");
        return Rows(physical);
    }

    Task ITableStore.DefineAsync(TablePlacement placement, string table, TableDefinition definition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = TablePolicy.Validate(definition);
        lock (_gate)
        {
            ThrowIfQueued();
            var key = (placement, table);
            if (_tables.TryGetValue(key, out var existing))
            {
                if (!TablePolicy.Compatible(existing.Definition, normalized))
                {
                    throw new PostgresQueryException("Incompatible existing table schema; migrations are not supported.");
                }
                return Task.CompletedTask;
            }
            _tables[key] = new TableEntry(normalized);
        }
        return Task.CompletedTask;
    }

    Task ITableStore.DropAsync(TablePlacement placement, string table, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfQueued();
            _tables.Remove((placement, table));
        }
        return Task.CompletedTask;
    }

    Task<bool> ITableStore.UpsertAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, TableValue[] values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfQueued();
            var entry = RequireTable(placement, table);
            var pk = CanonicalKey(definition, key);
            var nextRow = BuildRow(key, values);
            if (entry.Rows.TryGetValue(pk, out var existing))
            {
                if (values.Length == 0 || RowsEqual(existing, nextRow))
                {
                    return Task.FromResult(false);
                }
            }
            entry.Rows[pk] = nextRow;
            return Task.FromResult(true);
        }
    }

    Task<bool> ITableStore.DeleteAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfQueued();
            var entry = RequireTable(placement, table);
            return Task.FromResult(entry.Rows.Remove(CanonicalKey(definition, key)));
        }
    }

    Task<TableValue[]?> ITableStore.ReadAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfQueued();
            var entry = RequireTable(placement, table);
            return Task.FromResult(entry.Rows.TryGetValue(CanonicalKey(definition, key), out var row) ? CloneRow(row) : null);
        }
    }

    Task<TableValue[][]> ITableStore.PageAsync(TablePlacement placement, string table, TableDefinition definition, int offset, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfQueued();
            var entry = RequireTable(placement, table);
            return Task.FromResult(entry.OrderedRows().Skip(offset).Take(limit).Select(CloneRow).ToArray());
        }
    }

    private void ThrowIfQueued()
    {
        if (_failures.TryDequeue(out var error))
        {
            throw error;
        }
    }

    private TableEntry RequireTable(TablePlacement placement, string table)
        => _tables.TryGetValue((placement, table), out var entry)
            ? entry
            : throw new PostgresQueryException($"Table '{table}' does not exist.");

    private static string CanonicalKey(TableDefinition definition, TableValue[] key)
    {
        var byColumn = key.ToDictionary(v => v.Column, v => NormalizeJson(v.Json), StringComparer.Ordinal);
        return string.Join('\0', definition.PrimaryKey.Select(col => byColumn[col]));
    }

    private static string NormalizeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetRawText();
    }

    private static TableValue[] BuildRow(TableValue[] key, TableValue[] values)
        => [.. key.Concat(values).OrderBy(v => v.Column, StringComparer.Ordinal).Select(v => new TableValue(v.Column, NormalizeJson(v.Json)))];

    private static TableValue[] CloneRow(TableValue[] row)
        => [.. row.Select(v => new TableValue(v.Column, v.Json))];

    private static bool RowsEqual(TableValue[] left, TableValue[] right)
        => left.Length == right.Length && left.SequenceEqual(right);

    private sealed class TableEntry(TableDefinition definition)
    {
        public TableDefinition Definition { get; } = definition;
        public Dictionary<string, TableValue[]> Rows { get; } = new(StringComparer.Ordinal);

        public IEnumerable<TableValue[]> OrderedRows()
            => Rows.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value);
    }
}

internal sealed class SharedTablePlacements : ITablePlacements
{
    public Task<TablePlacement> Resolve(TableOwner owner, CancellationToken ct)
        => Task.FromResult(TablePlacement.Shared);
}
