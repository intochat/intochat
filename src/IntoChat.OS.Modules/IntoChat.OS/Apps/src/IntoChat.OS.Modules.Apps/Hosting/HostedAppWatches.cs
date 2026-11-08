using System.Reflection;
using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Modules.Apps;

// Serves an in-process app's declared watches (App.Watches) as the sandbox AppRunner does: one session subscription per watch
// through ISignalWatch (cursor-deduplicated, at least once, nothing missed while the host runs), each signal one turn of the app.
// A set watch subscribes to the members its Select returns and re-subscribes whenever its registry publishes ChangeType.
internal static class HostedAppWatches
{
    private static readonly MethodInfo FixedLoop = typeof(HostedAppWatches).GetMethod(nameof(ServeFixed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo SetLoop = typeof(HostedAppWatches).GetMethod(nameof(ServeSet), BindingFlags.NonPublic | BindingFlags.Static)!;

    // resolve returns a proxy of a contract for a neuron id; onError reports a failed handler or a failed subscription.
    // A failed subscription is opened again after a bounded backoff (Backoff by default: 1s, 2s, 4s ... capped at a minute)
    // until the app is unhosted; onRestart reports each restart with its attempt and delay.
    public static IAsyncDisposable Attach(ISignalWatch brain, AppTurns turns, AppAddress address,
        Func<Type, NeuronId, object> resolve, Action<Exception, string> onError,
        Action<string, int, TimeSpan>? onRestart = null, Func<int, TimeSpan>? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(onError);
        var retry = new Retry(backoff ?? Backoff, onRestart ?? ((_, _, _) => { }));
        var stopping = new CancellationTokenSource();
        var loops = new List<Task>();
        foreach (var watch in turns.App.DeclaredWatches())
        {
            var source = resolve(watch.SourceType, IdOf(watch.SourceType, address.Brain, watch.Key(address)));
            var serve = watch.IsSet
                ? SetLoop.MakeGenericMethod(watch.SourceType, watch.ChangeType!, watch.MemberType!, watch.SignalType)
                : FixedLoop.MakeGenericMethod(watch.SignalType);
            loops.Add((Task)serve.Invoke(null, [brain, turns, address, watch, source, resolve, onError, retry, stopping.Token])!);
        }
        return new Attached(stopping, loops);
    }

    // The delay before the attempt-th restart (from 1): doubling from a second, never more than a minute.
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 6))));

    private sealed record Retry(Func<int, TimeSpan> Backoff, Action<string, int, TimeSpan> OnRestart);

    internal static NeuronId IdOf(Type contract, string brain, string key)
    {
        var type = contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(false)?.Alias
            ?? throw new InvalidOperationException($"{contract.Name} names no neuron type.");
        return new NeuronId(new BrainId(brain), type, key);
    }

    private static Task ServeFixed<TSignal>(ISignalWatch brain, AppTurns turns, AppAddress address, AppWatch watch, object source,
        Func<Type, NeuronId, object> resolve, Action<Exception, string> onError, Retry retry, CancellationToken stopping) where TSignal : Signal
    {
        _ = address;
        _ = resolve;
        return Pump(() => brain.On<TSignal>(source, stopping), turns, watch, onError, retry, stopping);
    }

    private static Task ServeSet<TRegistry, TChange, TMember, TSignal>(ISignalWatch brain, AppTurns turns, AppAddress address, AppWatch watch,
        object source, Func<Type, NeuronId, object> resolve, Action<Exception, string> onError, Retry retry, CancellationToken stopping)
        where TRegistry : class where TChange : Signal where TMember : class where TSignal : Signal
    {
        IAsyncEnumerable<TSignal> Open()
        {
            var sources = Sources.Named<TRegistry, TChange, TMember>((TRegistry)source, async (registry, ct) =>
                [.. (await watch.Select!(registry, address, ct).ConfigureAwait(false))
                    .Select(key => (TMember)resolve(typeof(TMember), IdOf(typeof(TMember), address.Brain, key)))]);
            return Sources.Stream<TSignal>(brain, sources, stopping);
        }
        return Pump(Open, turns, watch, onError, retry, stopping);
    }

    // The next signal is read only after its turn ran, so turns keep the subscription's order; a failed handler is reported, not fatal.
    // A failed subscription is reported and opened again after the backoff (reset once a signal arrives) until stopping.
    private static async Task Pump<TSignal>(Func<IAsyncEnumerable<TSignal>> open, AppTurns turns, AppWatch watch, Action<Exception, string> onError,
        Retry retry, CancellationToken stopping) where TSignal : Signal
    {
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await foreach (var signal in open().WithCancellation(stopping).ConfigureAwait(false))
                {
                    failures = 0;
                    await turns.Enqueue(async _ =>
                    {
                        try { await watch.Handle(signal).ConfigureAwait(false); }
                        catch (Exception error) when (error is not OperationCanceledException) { onError(error, watch.Name); }
                    }, stopping).ConfigureAwait(false);
                }
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception error) { onError(error, watch.Name); }
            var delay = retry.Backoff(++failures);
            retry.OnRestart(watch.Name, failures, delay);
            try { await Task.Delay(delay, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed class Attached(CancellationTokenSource stopping, List<Task> loops) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(loops).ConfigureAwait(false); }
            finally { stopping.Dispose(); }
        }
    }
}
