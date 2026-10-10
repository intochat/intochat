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

    // SetLive throws on each sha in this set.
    public HashSet<string> DieOnSetLiveSha { get; } = [];

    public Task SetLive(string sha, CancellationToken ct)
    {
        if (DieOnSetLiveSha.Contains(sha))
        {
            return Task.FromException(new IOException("live.json could not be written: disk full"));
        }
        Current = sha;
        return Task.CompletedTask;
    }

    public string[] InstanceNames { get; set; } = ["mcp-a", "mcp-b", "silo-0", "silo-1"];
    public string? Self { get; set; }
    public List<string> RestartedSilos { get; } = [];
    // "name@sha": that instance never becomes ready on that build; "name@sha exits": it stops while starting.
    public HashSet<string> NeverHealthy { get; } = [];
    // An instance whose restart fails once, as if the activation driving the roll were cut off there.
    public string? InterruptAt { get; set; }
    // An instance whose restart always fails, like a CLI non-zero exit.
    public HashSet<string> NeverRestart { get; } = [];
    // An instance whose restart holds at its gate until the fact opens it.
    public Dictionary<string, RestartGate> RestartGates { get; } = [];
    public TaskCompletionSource<string> SelfRestarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Waited { get; } = [];
    public TaskCompletionSource<string> Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<SiloView[]> Silos(CancellationToken ct) =>
        Task.FromResult<SiloView[]>([.. InstanceNames.Where(name => name.StartsWith("silo-", StringComparison.Ordinal)).Select(name => new SiloView(name, Current, "Running", "Healthy"))]);

    public Task<string[]> Instances(CancellationToken ct) => Task.FromResult(InstanceNames);

    public async Task RestartInstance(string name, CancellationToken ct)
    {
        if (NeverRestart.Contains(name))
        {
            throw new InvalidOperationException($"aspire resource {name} restart exited 1: ...");
        }
        if (name == InterruptAt)
        {
            InterruptAt = null;
            Interrupted.TrySetResult(name);
            throw new InvalidOperationException($"cut off before restarting {name}");
        }
        RestartedSilos.Add($"{name}@{Current}");
        if (RestartGates.TryGetValue(name, out var gate))
        {
            gate.Reached.TrySetResult();
            await gate.Open.Task.WaitAsync(ct);
        }
        if (name == Self) { SelfRestarted.TrySetResult(name); }
    }

    public Task<SiloView> WaitInstance(string name, TimeSpan budget, CancellationToken ct)
    {
        Waited.Add($"{name}@{Current}");
        if (NeverHealthy.Contains($"{name}@{Current}"))
        {
            return Task.FromException<SiloView>(new InstanceNeverReadyException($"{name} not ready within {budget}", new TimeoutException()));
        }
        return NeverHealthy.Contains($"{name}@{Current} exits")
            ? Task.FromException<SiloView>(new InstanceNeverReadyException($"instance {name} stopped (Exited) while starting"))
            : Task.FromResult(new SiloView(name, Current, "Running", "Healthy"));
    }

    public Task RestartSilos(CancellationToken ct)
    {
        Restarts++;
        return DieOnRestart ? Task.FromException(new InvalidOperationException("silo died")) : Task.CompletedTask;
    }
}

internal sealed class RestartGate
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
