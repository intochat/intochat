using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

// Plain neuron classes: no Orleans namespace, no grain base. The host grains run them.

/// <summary>What the probes saw, keyed by neuron id; facts use unique keys so they never share an entry.</summary>
internal static class ProbeLog
{
    public static readonly ConcurrentDictionary<string, int> Touched = new();
    public static readonly ConcurrentDictionary<string, bool> Cancelled = new();
    public static readonly ConcurrentDictionary<string, bool> StreamStopped = new();
    public static readonly ConcurrentDictionary<string, bool> Fired = new();
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();

    public static TaskCompletionSource Gate(string id) => Gates.GetOrAdd(id, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(20, limit.Token); }
    }
}

[NeuronAlias("test.probe")]
public interface IProbe
{
    Task Touch();

    Task<int> Add(int a, int b);

    ValueTask<string> Echo(string text);

    ValueTask Nothing();

    Task<bool> WaitCancelled(CancellationToken cancellationToken);

    IAsyncEnumerable<int> Count(CancellationToken cancellationToken);

    [OneWayCall]
    Task Fire();

    [CallTimeout(1000)]
    Task Slow();

    Task<string> Key();

    Task<string> Caller();

    Task<string> Silo();

    Task<string> AskCaller(string otherKey);
}

[Neuron("probe")]
public sealed class ProbeNeuron : Neuron, IProbe
{
    private string Me => Id.ToString();

    public Task Touch()
    {
        ProbeLog.Touched.AddOrUpdate(Me, 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }

    public Task<int> Add(int a, int b) => Task.FromResult(a + b);

    public ValueTask<string> Echo(string text) => ValueTask.FromResult(text + "@" + Me);

    public ValueTask Nothing() => ValueTask.CompletedTask;

    public async Task<bool> WaitCancelled(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            ProbeLog.Cancelled[Me] = true;
            throw;
        }
    }

    public async IAsyncEnumerable<int> Count([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; ; i++)
            {
                yield return i;
                await Task.Delay(20, cancellationToken);
            }
        }
        finally { ProbeLog.StreamStopped[Me] = true; }
    }

    public async Task Fire()
    {
        await ProbeLog.Gate(Me).Task;
        ProbeLog.Fired[Me] = true;
    }

    public Task Slow() => Task.Delay(TimeSpan.FromSeconds(5));

    public Task<string> Key() => Task.FromResult(Me);

    public Task<string> Caller() => Task.FromResult(((IHostedNeuronContext)Context).Caller);

    public Task<string> Silo() => Task.FromResult(((IHostedNeuronContext)Context).Silo);

    public Task<string> AskCaller(string otherKey) => Get<IProbe>(new NeuronId(Id.Brain, "probe", otherKey)).Caller();
}

public interface IStatelessProbe
{
    Task<int> Busy();
}

[Neuron("probe.stateless"), StatelessNeuron]
public sealed class StatelessProbe : Neuron, IStatelessProbe
{
    private static int _running;

    private static int _most;

    public static int MostConcurrent => Volatile.Read(ref _most);

    public async Task<int> Busy()
    {
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, seen) != seen) { }
        await Task.Delay(300);
        Interlocked.Decrement(ref _running);
        return RuntimeHelpers.GetHashCode(this);
    }
}

/// <summary>Builds an invocation by hand, as a forged or misdirected caller would.</summary>
internal static class Calls
{
    public static RuntimeInvocation Of(string behavior, string key, string method)
        => new(behavior, NeuronProxy.ContractName(typeof(IProbe)), key, method, []);
}
