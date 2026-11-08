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
    public static IAsyncDisposable Attach(ISignalWatch brain, AppTurns turns, AppAddress address,
        Func<Type, NeuronId, object> resolve, Action<Exception, string> onError)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(onError);
        var stopping = new CancellationTokenSource();
        var loops = new List<Task>();
        foreach (var watch in turns.App.DeclaredWatches())
        {
            var source = resolve(watch.SourceType, IdOf(watch.SourceType, address.Brain, watch.Key(address)));
            var serve = watch.IsSet
                ? SetLoop.MakeGenericMethod(watch.SourceType, watch.ChangeType!, watch.MemberType!, watch.SignalType)
                : FixedLoop.MakeGenericMethod(watch.SignalType);
            loops.Add((Task)serve.Invoke(null, [brain, turns, address, watch, source, resolve, onError, stopping.Token])!);
        }
        return new Attached(stopping, loops);
    }

    internal static NeuronId IdOf(Type contract, string brain, string key)
    {
        var type = contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(false)?.Alias
            ?? throw new InvalidOperationException($"{contract.Name} names no neuron type.");
        return new NeuronId(new BrainId(brain), type, key);
    }

    private static Task ServeFixed<TSignal>(ISignalWatch brain, AppTurns turns, AppAddress address, AppWatch watch, object source,
        Func<Type, NeuronId, object> resolve, Action<Exception, string> onError, CancellationToken stopping) where TSignal : Signal
    {
        _ = address;
        _ = resolve;
        return Pump(brain.On<TSignal>(source, stopping), turns, watch, onError, stopping);
    }

    private static Task ServeSet<TRegistry, TChange, TMember, TSignal>(ISignalWatch brain, AppTurns turns, AppAddress address, AppWatch watch,
        object source, Func<Type, NeuronId, object> resolve, Action<Exception, string> onError, CancellationToken stopping)
        where TRegistry : class where TChange : Signal where TMember : class where TSignal : Signal
    {
        var sources = Sources.Named<TRegistry, TChange, TMember>((TRegistry)source, async (registry, ct) =>
            [.. (await watch.Select!(registry, address, ct).ConfigureAwait(false))
                .Select(key => (TMember)resolve(typeof(TMember), IdOf(typeof(TMember), address.Brain, key)))]);
        return Pump(Sources.Stream<TSignal>(brain, sources, stopping), turns, watch, onError, stopping);
    }

    // The next signal is read only after its turn ran, so turns keep the subscription's order; a failed handler is reported, not fatal.
    private static async Task Pump<TSignal>(IAsyncEnumerable<TSignal> signals, AppTurns turns, AppWatch watch, Action<Exception, string> onError,
        CancellationToken stopping) where TSignal : Signal
    {
        try
        {
            await foreach (var signal in signals.WithCancellation(stopping).ConfigureAwait(false))
            {
                await turns.Enqueue(async _ =>
                {
                    try { await watch.Handle(signal).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { onError(error, watch.Name); }
                }, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { onError(error, watch.Name); }
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
