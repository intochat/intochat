using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Postgres;

public sealed class PostgresModuleOptions : IModuleOptions
{
    public string ConnectionName { get; set; } = "postgres";
    public PostgresIsolation Isolation { get; set; } = PostgresIsolation.Shared;
    public PostgresResourceOptions Hosting { get; set; } = new();

    /// <summary>
    /// Derives the deterministic PostgreSQL database name (<c>brain_&lt;sha256-16&gt;</c>) for a brain or deployment id.
    /// Shared by both <c>BrainDatabases</c> and Aspire <c>InfrastructureBackingBuilder</c> to prevent normalization drift.
    /// </summary>
    public static string DeriveDatabaseName(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalized = id.Trim().ToLowerInvariant();
        return "brain_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    public PostgresModuleOptions WithConnection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ConnectionName = name;
        Hosting.Enabled = false;
        return this;
    }

    public PostgresModuleOptions WithPostgres(Action<PostgresResourceOptions>? configure = null)
    {
        Hosting.Enabled = true;
        configure?.Invoke(Hosting);
        return this;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionName);
        ArgumentNullException.ThrowIfNull(Hosting);
        ArgumentException.ThrowIfNullOrWhiteSpace(Hosting.DatabaseName);
    }
}

public sealed class PostgresResourceOptions
{
    public bool Enabled { get; set; }
    public string DatabaseName { get; set; } = "postgres";
    public bool PersistentStorage { get; set; } = true;
}
