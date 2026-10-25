using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Kernel.Composition;
using DB = DigitalBrain;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

// The DigitalBrain module the side-by-side facts host: a plain [Neuron] probe and the stand-in that admits plain subscribers
// of the legacy probe (LegacySource).
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

// The signal the legacy probe publishes, as plain neurons read it: same alias as the os record below.
[DB.Signal("probe.pinged")]
public sealed record Pinged(string Text) : DB.Signal;

public interface IPlainProbe
{
    Task<string> Echo(string text);
    IAsyncEnumerable<int> Count(int n, CancellationToken cancellationToken = default);
    Task<string> CallLegacy(string key, string text);
    IAsyncEnumerable<int> CountLegacy(string key, int n, CancellationToken cancellationToken = default);
    Task Follow(string source);
}

[DB.Neuron("probe.plain")]
public sealed class PlainProbe : DB.Neuron, IPlainProbe
{
    public Task<string> Echo(string text) => Task.FromResult(text + "|" + LegacyNeuronCalls.CallerOf());

    public async IAsyncEnumerable<int> Count(int n, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= n; i++) { await Task.Yield(); yield return i; }
    }

    public Task<string> CallLegacy(string key, string text) => Legacy(key).Echo(text);

    public IAsyncEnumerable<int> CountLegacy(string key, int n, CancellationToken cancellationToken = default) => Legacy(key).Count(n, cancellationToken);

    public Task Follow(string source) => Context.Synapses.Ensure<Pinged>(DB.NeuronId.Parse(source));

    [DB.On]
    public Task On(Pinged signal)
    {
        Heard.Add(Id.ToString(), signal.Text);
        return Task.CompletedTask;
    }

    private ILegacyProbe Legacy(string key) => Context.LegacyGet<ILegacyProbe>(new DB.NeuronId(Id.Brain, LegacyProbe.Type, key));
}

// Admits plain subscribers of the legacy probe; the legacy grain itself publishes.
[DB.Neuron(LegacyProbe.Type)]
public sealed class LegacyProbeSource : LegacySource;

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

// The legacy side: a Framework neuron on an Orleans grain interface, persisting through the cluster store declaration.
public interface ILegacyProbe : INeuron
{
    Task<string> Echo(string text);
    IAsyncEnumerable<int> Count(int n, CancellationToken cancellationToken = default);
    Task<string> CallPlain(string key, string text);
    IAsyncEnumerable<int> CountPlain(string key, int n, CancellationToken cancellationToken = default);
    Task Ping(string text);
    Task<int> Pings();
}

[GenerateSerializer, Alias("probe.legacy.state")]
public sealed class LegacyProbeState { [Id(0)] public int Pings { get; set; } }

[GenerateSerializer, Alias("probe.pinged")]
public sealed record LegacyPinged([property: Id(0)] string Text) : Signal;

[GrainType(Type)]
public sealed class LegacyProbe([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<LegacyProbeState> state)
    : IntoChat.OS.Framework.Neuron<LegacyProbeState>(state), ILegacyProbe
{
    public const string Type = "probe.legacy";

    public Task<string> Echo(string text) => Task.FromResult(text + "|" + LegacyNeuronCalls.CallerOf());

    public async IAsyncEnumerable<int> Count(int n, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= n; i++) { await Task.Yield(); yield return i; }
    }

    public Task<string> CallPlain(string key, string text) => Plain(key).Echo(text);

    public IAsyncEnumerable<int> CountPlain(string key, int n, CancellationToken cancellationToken = default) => Plain(key).Count(n, cancellationToken);

    public Task Ping(string text) => Save(new LegacyProbeState { Pings = Snapshot.Pings + 1 }, new LegacyPinged(text));

    public Task<int> Pings() => Task.FromResult(Snapshot.Pings);

    private IPlainProbe Plain(string key)
    {
        var brain = IntoChat.OS.Framework.BrainKey.BrainOf(this.GetPrimaryKeyString());
        return this.Get<IPlainProbe>(new DB.NeuronId(new DB.BrainId(brain), "probe.plain", key));
    }
}
