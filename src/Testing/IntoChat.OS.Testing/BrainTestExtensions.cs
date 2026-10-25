using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

public static class BrainTestExtensions
{
    public static async Task<SignalProbe<T>> Observe<T>(this IIntoChatOS brain, INeuron source, CancellationToken cancellationToken = default) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(brain);
        var subscriptions = brain as IBrainSubscriptions ?? throw new ArgumentException("Observe needs a brain that hands out subscription handles (the test harness brains do).", nameof(brain));
        var subscription = await subscriptions.SubscribeAsync<T>(source, cancellationToken).ConfigureAwait(false);
        var tracked = brain as ITrackedBrain;
        var probe = new SignalProbe<T>(subscription, tracked?.BufferCapacity ?? new SubscriptionOptions().BufferCapacity, tracked?.Execution ?? new());
        tracked?.Track(probe);
        return probe;
    }

    // Observe for a plain neuron (06-04): source is the proxy brain.Get returned, T one of its [Signal] records.
    public static async Task<PlainSignalProbe<T>> Observe<T>(this IIntoChatOS brain, object source, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(source);
        var tracked = brain as ITrackedBrain;
        var probe = await PlainSignalProbe<T>.Start(brain, source, tracked?.BufferCapacity ?? new SubscriptionOptions().BufferCapacity, tracked?.Execution ?? new(), cancellationToken).ConfigureAwait(false);
        tracked?.Track(probe);
        return probe;
    }

    // A plain neuron's held [Signal] records of type T, oldest first, read from its brain's log (06-04): what the os journal read
    // Read(from, count) gave for a grain neuron.
    public static async Task<T[]> History<T>(this ITestBrain brain, object neuron, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(brain);
        var target = neuron is DigitalBrain.Invocation.NeuronProxy proxy ? proxy.Target
            : throw new ArgumentException("Read the history of a plain neuron obtained from this brain's Get.", nameof(neuron));
        var signals = (IntoChat.OS.Runtime.IPlainSignals)brain.Services.GetService(typeof(IntoChat.OS.Runtime.IPlainSignals))!;
        var alias = IntoChat.OS.Runtime.PlainSignalTypes.AliasOf(typeof(T));
        List<T> found = [];
        for (long since = 0; ;)
        {
            var page = await signals.Read(target, since, DigitalBrain.SignalPage.MaxLimit, cancellationToken).ConfigureAwait(false);
            found.AddRange(page.Signals.Where(entry => entry.Alias == alias).Select(entry => IntoChat.OS.Runtime.PlainSignalTypes.Decode<T>(entry.Json)));
            if (page.Signals.Count == 0 || page.Signals[^1].Sequence >= page.Last) { return [.. found]; }
            since = page.Signals[^1].Sequence;
        }
    }

    // DeactivateAsync for a plain neuron (06-04): its runtime host grain is deactivated; the next call reactivates it from its state.
    public static Task DeactivateAsync(this ITestBrain brain, object neuron, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var target = neuron is DigitalBrain.Invocation.NeuronProxy proxy ? proxy.Target
            : throw new ArgumentException("Deactivate a plain neuron obtained from this brain's Get.", nameof(neuron));
        var grains = (IGrainFactory)brain.Services.GetService(typeof(IGrainFactory))!;
        return brain.DeactivateAsync(grains.GetGrain(global::Orleans.Runtime.GrainId.Create(HostTypeOf(target.Type), target.ToString())), cancellationToken);
    }

    // The runtime host grain type of a neuron type, by the runtime's policy (as the kernel composition's LegacyGrainSink reads it).
    private static string HostTypeOf(string type)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException error) { types = [.. error.Types.OfType<Type>()]; }
            foreach (var candidate in types)
            {
                if (candidate.IsAbstract || !typeof(DigitalBrain.Neuron).IsAssignableFrom(candidate)) { continue; }
                if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<DigitalBrain.NeuronAttribute>(candidate, inherit: false)?.Type != type) { continue; }
                if (candidate.IsDefined(typeof(DigitalBrain.StatelessNeuronAttribute), inherit: false)) { return "neuron.host.stateless"; }
                return candidate.IsDefined(typeof(DigitalBrain.ReentrantAttribute), inherit: true) ? "neuron.host.reentrant" : "neuron.host";
            }
        }
        throw new ArgumentException($"No plain neuron class has type '{type}'.", nameof(type));
    }

    public static BehaviorRun RunBehavior(this IIntoChatOS brain, Func<IIntoChatOS, CancellationToken, Task> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var run = new BehaviorRun(brain, body, cancellationToken);
        (brain as ITrackedBrain)?.Track(run);
        return run;
    }
}
