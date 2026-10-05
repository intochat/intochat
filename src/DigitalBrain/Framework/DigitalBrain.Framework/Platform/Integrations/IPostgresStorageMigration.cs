namespace DigitalBrain.Postgres;

[Alias("DigitalBrain.Postgres.IPostgresStorageMigration")]
[PlatformOnly]
public interface IPostgresStorageMigration : INeuron
{
    Task Ensure();
    [Orleans.Concurrency.ReadOnly] Task<string[]> ReadSkippedBlobs();
}
