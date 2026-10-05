using DigitalBrain;
using DigitalBrain.Kernel;

namespace DigitalBrain.Postgres;

[PlatformOnly]
internal interface IPostgresLegacyOwner : INeuron
{
    Task<string[]> ReadLegacyTables();
}

[PlatformOnly]
internal interface IPostgresTableOwnership : INeuron
{
    Task Transfer(string previousOwner, string owner);
}
