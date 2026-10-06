using DigitalBrain;

namespace DigitalBrain.Postgres;

// An app's table index, keyed by the app's owner key.
[PlatformOnly]
public interface IPostgresTables : INeuron
{
    [Orleans.Concurrency.ReadOnly] Task<string[]> ReadTables();
    [Orleans.Concurrency.AlwaysInterleave]
    Task Register(string tableId);
    [Orleans.Concurrency.AlwaysInterleave]
    Task RequireOpen();
    Task Retire();
}

[PlatformOnly]
public interface IPostgresTableLifetime : INeuron
{
    Task Retire(string owner);
}
