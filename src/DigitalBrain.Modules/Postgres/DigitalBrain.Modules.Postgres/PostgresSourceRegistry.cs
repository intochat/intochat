using System.Collections.Concurrent;
using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DigitalBrain.Postgres;

internal static class PostgresCapacityKind
{
    public const string Kind = "postgres";
    public const string PlatformOrigin = "platform";
    public const string DatabasePrefix = "db:";

    // A table pinned before origins existed (Origin == null) lives in the platform source.
    public static string OriginOrPlatform(string? origin) => string.IsNullOrEmpty(origin) ? PlatformOrigin : origin;
}

// Hosted runs can provision one database per brain; other deployments use the configured source.
internal sealed class PostgresConfiguredSource(bool active) : ICapacityConfiguredSource
{
    public string Kind => active ? PostgresCapacityKind.Kind : PostgresCapacityKind.Kind + ":inactive";
    public string Origin => PostgresCapacityKind.PlatformOrigin;
}

internal interface IPostgresSourceRegistry
{
    Task<NpgsqlDataSource> Get(string origin, CancellationToken ct = default);
}

internal sealed class PostgresSourceRegistry(IModuleIntegrations<PostgresModule> integrations, IOptions<PostgresModuleOptions> options) : IPostgresSourceRegistry, IAsyncDisposable, IIntegrationStartup
{
    private readonly ConcurrentDictionary<(long Revision, string Origin), NpgsqlDataSource> _sources = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlConnectionStringBuilder? _platform;
    private NpgsqlConnectionStringBuilder? _admin;
    private long _revision = -1;
    private bool _adminMaterialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
        => _ = await Get(PostgresCapacityKind.PlatformOrigin, cancellationToken);

    public async Task<NpgsqlDataSource> Get(string origin, CancellationToken ct = default)
    {
        if (origin != PostgresCapacityKind.PlatformOrigin && !origin.StartsWith(PostgresCapacityKind.DatabasePrefix, StringComparison.Ordinal))
        { throw new PostgresUnavailableException("Unknown Postgres capacity origin."); }
        await _gate.WaitAsync(ct);
        try
        {
            var handle = integrations.Get("postgres");
            var snapshot = await handle.ReadAsync(ct);
            if (snapshot.Status != IntegrationStatus.Ready) { throw new PostgresUnavailableException("The Postgres integration is unavailable."); }
            var needAdmin = options.Value.ProvisionPerBrain || origin != PostgresCapacityKind.PlatformOrigin;
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
            var settings = origin == PostgresCapacityKind.PlatformOrigin ? _platform : _admin
                ?? throw new PostgresUnavailableException("This table's database is not reachable from this deployment.");
            var connection = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
            if (origin != PostgresCapacityKind.PlatformOrigin) { connection.Database = origin[PostgresCapacityKind.DatabasePrefix.Length..]; }
            return _sources.GetOrAdd((_revision, origin), _ => NpgsqlDataSource.Create(connection.ConnectionString));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values) { await source.DisposeAsync(); }
    }
}
