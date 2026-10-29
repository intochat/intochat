using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

public static class BrainTestExtensions
{
    // Observe a neuron: source is the reference brain.Get returned, T one of its [Signal] records.
    public static async Task<PlainSignalProbe<T>> Observe<T>(this IIntoChatOS brain, object source, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(source);
        var tracked = brain as ITrackedBrain;
        var probe = await PlainSignalProbe<T>.Start(brain, source, tracked?.BufferCapacity ?? ITrackedBrain.DefaultBufferCapacity, tracked?.Execution ?? new(), cancellationToken).ConfigureAwait(false);
        tracked?.Track(probe);
        return probe;
    }

    // Observe for every signal of several [Signal] types a plain neuron publishes, in order (06-06d).
    public static async Task<PlainSignalProbe<DigitalBrain.Signal>> ObserveAll(this ITestBrain brain, object source, IReadOnlyList<Type> signals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(signals);
        var plain = (IntoChat.OS.Runtime.IPlainSignals)brain.Services.GetService(typeof(IntoChat.OS.Runtime.IPlainSignals))!;
        var tracked = brain as ITrackedBrain;
        var probe = await PlainSignalProbe<DigitalBrain.Signal>.StartAll(plain, source, signals, tracked?.BufferCapacity ?? ITrackedBrain.DefaultBufferCapacity,
            tracked?.Execution ?? new(), cancellationToken).ConfigureAwait(false);
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
        return brain.DeactivateAsync(IntoChat.OS.Kernel.Composition.KernelTestHooks.Host(brain.Services, target), cancellationToken);
    }

    public static BehaviorRun RunBehavior(this IIntoChatOS brain, Func<IIntoChatOS, CancellationToken, Task> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var run = new BehaviorRun(brain, body, cancellationToken);
        (brain as ITrackedBrain)?.Track(run);
        return run;
    }
}
