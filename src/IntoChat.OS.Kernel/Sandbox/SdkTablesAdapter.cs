using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Kernel-side bridge for <see cref="IntoChat.Sdk.ITables"/>: delegates to <see cref="ITable"/> when
/// Postgres is active and seamlessly persists rows per app/table locally when running without Postgres.
/// </summary>
internal sealed class SdkTablesAdapter(IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, LocalTableState> _localTables = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _postgresLock = new(1, 1);
    private ILogger Log => services.GetService<ILoggerFactory>()?.CreateLogger<SdkTablesAdapter>() ?? OsConsoleLog.For<SdkTablesAdapter>();

    public Task EnsureAsync(string brainId, string appKey, string tableName, IntoChat.Sdk.TableColumnSpec[] columns, CancellationToken cancellationToken)
    {
        var key = ScopeKey(brainId, appKey, tableName);
        var state = _localTables.GetOrAdd(key, _ => new LocalTableState());
        state.SetColumns(columns);
        return TryDefinePostgresAsync(brainId, appKey, tableName, columns, cancellationToken);
    }

    public async Task UpsertAsync(string brainId, string appKey, string tableName, string rowKey, string jsonRow, CancellationToken cancellationToken)
    {
        var key = ScopeKey(brainId, appKey, tableName);
        var state = _localTables.GetOrAdd(key, _ => new LocalTableState());
        state.Upsert(rowKey, jsonRow);
        await TryUpsertPostgresAsync(brainId, appKey, tableName, rowKey, jsonRow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string brainId, string appKey, string tableName, string rowKey, CancellationToken cancellationToken)
    {
        if (await TryReadPostgresAsync(brainId, appKey, tableName, rowKey, cancellationToken).ConfigureAwait(false) is { } pgRow)
        {
            return pgRow;
        }

        var key = ScopeKey(brainId, appKey, tableName);
        return _localTables.TryGetValue(key, out var state) ? state.Get(rowKey) : null;
    }

    public async Task<IntoChat.Sdk.TableRow[]> ListAsync(string brainId, string appKey, string tableName, int offset, int limit, CancellationToken cancellationToken)
    {
        if (await TryListPostgresAsync(brainId, appKey, tableName, offset, limit, cancellationToken).ConfigureAwait(false) is { } pgRows)
        {
            return pgRows;
        }

        var key = ScopeKey(brainId, appKey, tableName);
        return _localTables.TryGetValue(key, out var state)
            ? state.List(offset, limit)
            : [];
    }

    public async Task<bool> DeleteAsync(string brainId, string appKey, string tableName, string rowKey, CancellationToken cancellationToken)
    {
        var key = ScopeKey(brainId, appKey, tableName);
        var localDeleted = _localTables.TryGetValue(key, out var state) && state.Delete(rowKey);
        var pgDeleted = await TryDeletePostgresAsync(brainId, appKey, tableName, rowKey, cancellationToken).ConfigureAwait(false);
        return pgDeleted || localDeleted;
    }

    private static string ScopeKey(string brainId, string appKey, string tableName)
        => AppRef.Of(new BrainId(brainId), appKey).ChildKey(SanitizeName(tableName));

    private static string SanitizeName(string tableName)
        => string.IsNullOrWhiteSpace(tableName)
            ? throw new ArgumentException("Table name is required.", nameof(tableName))
            : new string(tableName.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());

    private bool IsPostgresActive()
        => services.GetService<ModuleInventory>() is { } inventory
            && inventory.Modules.Any(m => string.Equals(m.Id.Value, "postgres", StringComparison.OrdinalIgnoreCase));

    private async Task TryDefinePostgresAsync(string brainId, string appKey, string tableName, IntoChat.Sdk.TableColumnSpec[] columns, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive())
        {
            return;
        }

        try
        {
            var table = SiloNeurons.Get<ITable>(services, ScopeKey(brainId, appKey, tableName));
            var definition = new TableDefinition(
                [new TableColumn("key", "text"), new TableColumn("payload", "jsonb"), .. columns.Select(c => new TableColumn(SanitizeName(c.Name), "text"))],
                ["key"]);
            await table.Define(definition).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPostgresUnavailable(ex))
        {
            Log.LogSwallowed(ex, "defining optional Postgres table fallback");
        }
    }

    private async Task TryUpsertPostgresAsync(string brainId, string appKey, string tableName, string rowKey, string jsonRow, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive())
        {
            return;
        }

        try
        {
            await _postgresLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var table = SiloNeurons.Get<ITable>(services, ScopeKey(brainId, appKey, tableName));
                var keyJson = JsonSerializer.Serialize(rowKey);
                await table.Upsert(
                    [new TableValue("key", keyJson)],
                    [new TableValue("key", keyJson), new TableValue("payload", string.IsNullOrWhiteSpace(jsonRow) ? "{}" : jsonRow)])
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _postgresLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPostgresUnavailable(ex))
        {
            Log.LogSwallowed(ex, "upserting optional Postgres table fallback");
        }
    }

    private async Task<string?> TryReadPostgresAsync(string brainId, string appKey, string tableName, string rowKey, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive())
        {
            return null;
        }

        try
        {
            var table = SiloNeurons.Get<ITable>(services, ScopeKey(brainId, appKey, tableName));
            var row = await table.Read([new TableValue("key", JsonSerializer.Serialize(rowKey))])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return row?.FirstOrDefault(v => string.Equals(v.Column, "payload", StringComparison.Ordinal))?.Json;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPostgresUnavailable(ex))
        {
            Log.LogSwallowed(ex, "reading optional Postgres table fallback");
            return null;
        }
    }

    private async Task<IntoChat.Sdk.TableRow[]?> TryListPostgresAsync(string brainId, string appKey, string tableName, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive())
        {
            return null;
        }

        try
        {
            var table = SiloNeurons.Get<ITable>(services, ScopeKey(brainId, appKey, tableName));
            var page = await table.Page(Math.Max(0, offset), Math.Clamp(limit, 1, 200))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return [.. page.Select(row =>
            {
                var keyRaw = row.FirstOrDefault(v => string.Equals(v.Column, "key", StringComparison.Ordinal))?.Json ?? "\"\"";
                var decodedKey = JsonSerializer.Deserialize<string>(keyRaw) ?? keyRaw;
                var json = row.FirstOrDefault(v => string.Equals(v.Column, "payload", StringComparison.Ordinal))?.Json ?? "{}";
                return new IntoChat.Sdk.TableRow(decodedKey, json);
            })];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPostgresUnavailable(ex))
        {
            Log.LogSwallowed(ex, "listing optional Postgres table fallback");
            return null;
        }
    }

    private async Task<bool> TryDeletePostgresAsync(string brainId, string appKey, string tableName, string rowKey, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive())
        {
            return false;
        }

        try
        {
            var table = SiloNeurons.Get<ITable>(services, ScopeKey(brainId, appKey, tableName));
            return await table.Delete([new TableValue("key", JsonSerializer.Serialize(rowKey))])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPostgresUnavailable(ex))
        {
            Log.LogSwallowed(ex, "deleting optional Postgres table fallback");
            return false;
        }
    }

    private static bool IsPostgresUnavailable(Exception exception)
        => exception is InvalidOperationException
            or KeyNotFoundException
            or PostgresQueryException
            or PostgresUnavailableException
            or NotSupportedException
            or TimeoutException
            or Orleans.Runtime.OrleansException
            or UnauthorizedAccessException;

    private sealed class LocalTableState
    {
        private readonly ConcurrentDictionary<string, string> _rows = new(StringComparer.Ordinal);
        private IntoChat.Sdk.TableColumnSpec[] _columns = [];

        public void SetColumns(IntoChat.Sdk.TableColumnSpec[] columns) => _columns = columns;

        public void Upsert(string key, string json)
        {
            _ = _columns;
            _rows[key] = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }

        public string? Get(string key) => _rows.TryGetValue(key, out var json) ? json : null;

        public IntoChat.Sdk.TableRow[] List(int offset, int limit)
            => [.. _rows.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Skip(Math.Max(0, offset))
                .Take(Math.Clamp(limit, 1, 200))
                .Select(pair => new IntoChat.Sdk.TableRow(pair.Key, pair.Value))];

        public bool Delete(string key) => _rows.TryRemove(key, out _);
    }
}
