using DigitalBrain.Microsoft.Aspire.Cluster;

namespace DigitalBrain.Microsoft.Aspire.Tests;

internal sealed class FakeClusterHost : IClusterHost
{
    public string Current { get; set; } = "base000";
    public int Restarts { get; private set; }
    public bool DieOnRestart { get; set; }

    public Task<string> CurrentSha(CancellationToken ct) => Task.FromResult(Current);

    public Task SetLive(string sha, CancellationToken ct)
    {
        Current = sha;
        return Task.CompletedTask;
    }

    public Task<SiloView[]> Silos(CancellationToken ct) =>
        Task.FromResult<SiloView[]>([new("silo-0", Current, "Running", "Healthy"), new("silo-1", Current, "Running", "Healthy")]);

    public Task RestartSilos(CancellationToken ct)
    {
        Restarts++;
        return DieOnRestart ? Task.FromException(new InvalidOperationException("silo died")) : Task.CompletedTask;
    }
}
