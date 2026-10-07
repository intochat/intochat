using DigitalBrain.Postgres.Tables;
using System.Collections.Concurrent;
using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DigitalBrain.Postgres;

public static class PostgresCapacityKind
{
    public const string Kind = "postgres";
    public const string PlatformOrigin = "platform";
    public const string DatabasePrefix = "db:";
}

// Hosted runs can provision one database per brain; other deployments use the configured source.
internal sealed class PostgresConfiguredSource(bool active) : ICapacityConfiguredSource
{
    public string Kind => active ? PostgresCapacityKind.Kind : PostgresCapacityKind.Kind + ":inactive";
    public string Origin => PostgresCapacityKind.PlatformOrigin;
}

public interface IPostgresSourceRegistry
{
    Task<NpgsqlDataSource> Get(TablePlacement placement, CancellationToken ct = default);
}

internal sealed class PostgresSourceRegistry(IModuleIntegrations<PostgresModule> integrations, IOptions<PostgresModuleOptions> options) : IPostgresSourceRegistry, IAsyncDisposable, IIntegrationStartup
{
    private readonly ConcurrentDictionary<(long Revision, TablePlacement Placement), NpgsqlDataSource> _sources = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlConnectionStringBuilder? _platform;
    private NpgsqlConnectionStringBuilder? _admin;
    private long _revision = -1;
    private bool _adminMaterialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
        => _ = await Get(TablePlacement.Shared, cancellationToken);

    // Serves the deployment's own placements; a Connection placement runs inside its connection neuron.
    public async Task<NpgsqlDataSource> Get(TablePlacement placement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.Kind == TablePlacementKind.Connection || placement.Kind == TablePlacementKind.BrainDatabase && string.IsNullOrEmpty(placement.Name))
        { throw new ArgumentException("The deployment's Postgres sources serve only Shared and BrainDatabase placements.", nameof(placement)); }
        await _gate.WaitAsync(ct);
        try
        {
            var handle = integrations.Get("postgres");
            var snapshot = await handle.ReadAsync(ct);
            if (snapshot.Status != IntegrationStatus.Ready) { throw new PostgresUnavailableException("The Postgres integration is unavailable."); }
            var needAdmin = options.Value.ProvisionPerBrain || placement.Kind == TablePlacementKind.BrainDatabase;
            if (_platform is null || _revision != snapshot.Revision || needAdmin && !_adminMaterialized)
            {
                var released = await handle.ReleaseAsync(ct);
                if (released.Snapshot.Status != IntegrationStatus.Ready) { throw new PostgresUnavailableException("The Postgres integration is unavailable."); }
                var platform = PostgresConnectionSettings.Parse(released.Values["ConnectionString"]);
                var admin = needAdmin && released.Values.TryGetValue("AdminConnection", out var value) ? PostgresConnectionSettings.Parse(value) : null;
                if (options.Value.ProvisionPerBrain && admin is null) { throw new InvalidOperationException("AdminConnection is required for per-brain provisioning."); }
                _platform = platform;
                _admin = admin;
                _adminMaterialized = needAdmin;
                _revision = released.Snapshot.Revision;
            }
            var settings = placement.Kind == TablePlacementKind.Shared ? _platform : _admin
                ?? throw new PostgresUnavailableException("This table's database is not reachable from this deployment.");
            var connection = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            if (placement.Kind == TablePlacementKind.BrainDatabase) { connection.Database = placement.Name; }
            return _sources.GetOrAdd((_revision, placement), _ => NpgsqlDataSource.Create(connection.ConnectionString));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values) { await source.DisposeAsync(); }
    }
}
