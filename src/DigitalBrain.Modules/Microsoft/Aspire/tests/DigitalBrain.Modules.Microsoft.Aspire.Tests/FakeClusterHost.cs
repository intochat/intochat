using DigitalBrain.Microsoft.Aspire.Cluster;

namespace DigitalBrain.Microsoft.Aspire.Tests;

internal sealed class FakeClusterHost : IClusterHost
{
    public string Current { get; set; } = "base000";
    public int Restarts { get; private set; }
    public bool DieOnRestart { get; set; }

    public Dictionary<string, int> SpanCounts { get; } = [];

    public Task<int> Spans(string service, string span, CancellationToken ct) =>
        Task.FromResult(SpanCounts.GetValueOrDefault($"{service} {span}"));

    public Task<string> CurrentSha(CancellationToken ct) => Task.FromResult(Current);

    public Task SetLive(string sha, CancellationToken ct)
    {
        Current = sha;
        return Task.CompletedTask;
    }

    public string[] InstanceNames { get; set; } = ["mcp-a", "mcp-b", "silo-0", "silo-1"];
    public string? Self { get; set; }
    public List<string> RestartedSilos { get; } = [];
    public HashSet<string> NeverHealthy { get; } = [];
    // An instance whose restart fails once, as if the activation driving the roll were cut off there.
    public string? InterruptAt { get; set; }

    public Task<SiloView[]> Silos(CancellationToken ct) =>
        Task.FromResult<SiloView[]>([.. InstanceNames.Where(name => name.StartsWith("silo-", StringComparison.Ordinal)).Select(name => new SiloView(name, Current, "Running", "Healthy"))]);

    public Task<string[]> Instances(CancellationToken ct) => Task.FromResult(InstanceNames);

    public Task RestartSilo(string name, CancellationToken ct)
    {
        if (name == InterruptAt)
        {
            InterruptAt = null;
            return Task.FromException(new InvalidOperationException($"cut off before restarting {name}"));
        }
        RestartedSilos.Add($"{name}@{Current}");
        return Task.CompletedTask;
    }

    public Task<SiloView> WaitSilo(string name, TimeSpan budget, CancellationToken ct) =>
        NeverHealthy.Contains(name)
            ? Task.FromException<SiloView>(new TimeoutException($"{name} not ready within {budget}"))
            : Task.FromResult(new SiloView(name, Current, "Running", "Healthy"));

    public Task RestartSilos(CancellationToken ct)
    {
        Restarts++;
        return DieOnRestart ? Task.FromException(new InvalidOperationException("silo died")) : Task.CompletedTask;
    }
}
