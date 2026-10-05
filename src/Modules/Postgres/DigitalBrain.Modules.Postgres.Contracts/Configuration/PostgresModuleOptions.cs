using DigitalBrain;

namespace DigitalBrain.Postgres;

public sealed class PostgresModuleOptions : IModuleOptions
{
    // Hosted provisioning hands the platform server's admin connection to the brain under this
    // key; its presence switches capacity to per-brain provisioning.
    public const string AdminConnectionKey = "DigitalBrain:Capacity:Postgres:AdminConnection";


    public string ConnectionName { get; set; } = "postgres";
    public PostgresResourceOptions Hosting { get; set; } = new();

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
