
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Postgres.Tables;

// One index per TableOwner: which tables the app defined, and whether its storage is retired.
[PlatformOnly]
public interface ITableIndex : INeuron
{
    [Orleans.Concurrency.ReadOnly] Task<string[]> ReadTables();
    [Orleans.Concurrency.AlwaysInterleave] Task Register(string tableId);
    [Orleans.Concurrency.AlwaysInterleave] Task RequireOpen();
    Task Retire();
}

[PlatformOnly]
public interface ITableLifetime : INeuron
{
    Task Retire(string owner);
}
