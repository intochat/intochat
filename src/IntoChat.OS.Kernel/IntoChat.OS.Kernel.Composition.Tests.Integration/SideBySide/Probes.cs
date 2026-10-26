using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Kernel.Composition;
using DB = DigitalBrain;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

// The DigitalBrain module the composition facts host: a plain [Neuron] probe that publishes, follows and counts.
[DB.Module("probe-plain")]
public sealed class PlainProbeModule : DB.Module
{
    public static DB.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

// A module that needs a sandbox, for the start-time Need check.
[DB.Module("probe-sandbox")]
public sealed class SandboxProbeModule : DB.Module
{
    public static DB.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [DB.Need.Sandbox], Settings: [], Secrets: []);
}

[DB.Signal("probe.pinged")]
public sealed record Pinged(string Text) : DB.Signal;

public interface IPlainProbe
{
    Task<string> Echo(string text);
    IAsyncEnumerable<int> Count(int n, CancellationToken cancellationToken = default);
    Task Follow(string source);
    Task Ping(string text);
}

[DB.Neuron("probe.plain")]
public sealed class PlainProbe : DB.Neuron, IPlainProbe
{
    // The caller's neuron id, as the runtime carries it (empty when the call came from no neuron).
    public Task<string> Echo(string text) => Task.FromResult(text + "|" + (Orleans.Runtime.RequestContext.Get(KernelInvocationSink.CallerKey) as string ?? string.Empty));

    public async IAsyncEnumerable<int> Count(int n, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= n; i++) { await Task.Yield(); yield return i; }
    }

    public Task Follow(string source) => Context.Synapses.Ensure<Pinged>(DB.NeuronId.Parse(source));

    [DB.On]
    public Task On(Pinged signal)
    {
        Heard.Add(Id.ToString(), signal.Text);
        return Task.CompletedTask;
    }

    public Task Ping(string text) => Publish(new Pinged(text));
}

public static class Heard
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Texts = new(StringComparer.Ordinal);

    public static void Add(string neuron, string text) => Texts.GetOrAdd(neuron, static _ => new()).Enqueue(text);

    public static string[] Of(string neuron) => Texts.TryGetValue(neuron, out var texts) ? [.. texts] : [];

    public static async Task Until(string neuron, int count, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (Of(neuron).Length < count) { await Task.Delay(50, timeout.Token); }
    }
}

// 06-06d: a plain neuron with state, for the kernel's instance directory (Kernel.Instances<T> on both runtimes).
[DigitalBrain.Contracts.DefaultNeuronType(PlainCounted.Type)]
public interface IPlainCounted
{
    Task Add();
    [DB.ReadOnlyCall]
    Task<int> Count();
    Task Forget();
}

public sealed record PlainCountedState(int Count);

[DB.Neuron(Type)]
public sealed class PlainCounted : DB.Neuron, IPlainCounted
{
    public const string Type = "probe.counted";

    public async Task Add() => await Context.State.Save(new PlainCountedState(await Count() + 1), []);

    public async Task<int> Count() => (await Context.State.Load<PlainCountedState>())?.Count ?? 0;

    public Task Forget() => Context.State.Clear();
}
