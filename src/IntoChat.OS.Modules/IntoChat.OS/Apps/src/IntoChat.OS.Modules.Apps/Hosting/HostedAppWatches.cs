using System.Reflection;
using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Spi;

namespace IntoChat.OS.Modules.Apps;

// Serves an in-process app's declared watches (App.Watches) through cluster listeners, as the sandbox AppRunner serves them through
// streams: each delivered signal is one turn of the app. A fixed watch takes signals its one neuron publishes; a set watch takes
// signals from the members its Select returns, and selects again whenever its registry publishes the watch's ChangeType.
internal static class HostedAppWatches
{
    private static readonly MethodInfo ListenFixed = typeof(HostedAppWatches).GetMethod(nameof(AttachFixed), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ListenSet = typeof(HostedAppWatches).GetMethod(nameof(AttachSet), BindingFlags.NonPublic | BindingFlags.Static)!;

    // resolve returns a proxy of the contract for a neuron id; onError reports a selection or handler failure.
    public static async Task<IAsyncDisposable> Attach(IClusterListeners listeners, AppTurns turns, AppAddress address,
        Func<Type, NeuronId, object> resolve, Action<Exception, string> onError)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(onError);
        var registrations = new List<IAsyncDisposable>();
        foreach (var watch in turns.App.DeclaredWatches())
        {
            var source = IdOf(watch.SourceType, address.Brain, watch.Key(address));
            var attach = watch.IsSet
                ? ListenSet.MakeGenericMethod(watch.ChangeType!, watch.SignalType)
                : ListenFixed.MakeGenericMethod(watch.SignalType);
            var task = (Task<IAsyncDisposable[]>)attach.Invoke(null, [listeners, turns, address, watch, source, resolve, onError])!;
            registrations.AddRange(await task.ConfigureAwait(false));
        }
        return new Registrations(registrations);
    }

    internal static NeuronId IdOf(Type contract, string brain, string key)
    {
        var type = contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(false)?.Alias
            ?? throw new InvalidOperationException($"{contract.Name} names no neuron type.");
        return new NeuronId(new BrainId(brain), type, key);
    }

    private static string ListenerOf(AppAddress address, AppWatch watch, string part) => $"watch:{address.App}:{watch.Name}:{part}";

    private static Task Turn<TSignal>(AppTurns turns, AppWatch watch, TSignal signal, Action<Exception, string> onError) where TSignal : Signal
        => turns.Enqueue(async _ =>
        {
            try { await watch.Handle(signal).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { onError(error, watch.Name); }
        });

    private static async Task<IAsyncDisposable[]> AttachFixed<TSignal>(IClusterListeners listeners, AppTurns turns, AppAddress address, AppWatch watch,
        NeuronId source, Func<Type, NeuronId, object> resolve, Action<Exception, string> onError) where TSignal : Signal
    {
        _ = resolve;
        return [await listeners.Listen<TSignal>(ListenerOf(address, watch, "signal"),
            (publisher, signal, _) => publisher == source ? Turn(turns, watch, signal, onError) : Task.CompletedTask).ConfigureAwait(false)];
    }

    private static async Task<IAsyncDisposable[]> AttachSet<TChange, TSignal>(IClusterListeners listeners, AppTurns turns, AppAddress address, AppWatch watch,
        NeuronId registry, Func<Type, NeuronId, object> resolve, Action<Exception, string> onError) where TChange : Signal where TSignal : Signal
    {
        var members = new MemberSet();
        async Task SelectAsync()
        {
            try
            {
                var keys = await watch.Select!(resolve(watch.SourceType, registry), address, CancellationToken.None).ConfigureAwait(false);
                members.Replace([.. keys.Select(key => IdOf(watch.MemberType!, address.Brain, key))]);
            }
            catch (Exception error) when (error is not OperationCanceledException) { onError(error, watch.Name); }
        }
        // Listen first, then select: a change published while selecting selects again rather than being missed.
        var change = await listeners.Listen<TChange>(ListenerOf(address, watch, "change"),
            (publisher, _, _) => publisher == registry ? SelectAsync() : Task.CompletedTask).ConfigureAwait(false);
        var signals = await listeners.Listen<TSignal>(ListenerOf(address, watch, "signal"),
            (publisher, signal, _) => members.Contains(publisher) ? Turn(turns, watch, signal, onError) : Task.CompletedTask).ConfigureAwait(false);
        await SelectAsync().ConfigureAwait(false);
        return [change, signals];
    }

    private sealed class MemberSet
    {
        private volatile NeuronId[] _members = [];

        public void Replace(NeuronId[] members) => _members = members;

        public bool Contains(NeuronId id) => Array.IndexOf(_members, id) >= 0;
    }

    private sealed class Registrations(List<IAsyncDisposable> registrations) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var registration in registrations) { await registration.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
