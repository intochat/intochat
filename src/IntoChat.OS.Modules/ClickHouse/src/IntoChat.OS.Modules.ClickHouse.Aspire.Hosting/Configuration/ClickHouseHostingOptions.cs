namespace IntoChat.OS.Modules.ClickHouse.Aspire.Hosting;

public sealed class ClickHouseHostingOptions
{
    private readonly List<string> _seeds = [];

    // Embedded init scripts under Seeds/, copied into /docker-entrypoint-initdb.d in the order added.
    public IReadOnlyList<string> Seeds => _seeds;

    // Dev only: re-run init scripts on every container start instead of only on an empty data dir.
    public bool AlwaysRunInitScripts { get; set; }
    public bool PersistentStorage { get; set; } = true;
    public string DatabaseName { get; set; } = new ClickHouseModuleOptions().DatabaseName;

    public ClickHouseHostingOptions WithSeed(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_seeds.Contains(name, StringComparer.Ordinal))
        {
            _seeds.Add(name);
        }

        return this;
    }
}
