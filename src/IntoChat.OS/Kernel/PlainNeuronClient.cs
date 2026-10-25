using System.Reflection;
using IntoChat.OS.Runtime;

namespace IntoChat.OS;

// Transitional (06-07, folded into IIntoChatOS in 06-08): Get and On for plain neurons, whose contracts are plain interfaces
// (not INeuron) and whose signals are DigitalBrain [Signal] records (not Signal). C# drops IIntoChatOS's own Get<T> and On<T>
// when T breaks their constraints, so brain.Get<ITimer>(id) and brain.On<TimerTick>(timer) bind here and a program reads the
// same whether the neuron is still a grain or already plain.
public static class PlainNeuronClient
{
    private static readonly MethodInfo LegacyGet = typeof(IIntoChatOS).GetMethod(nameof(IIntoChatOS.Get))!;

    // A plain contract resolves through the brain's plain path; a contract that is still INeuron (reached from generic code
    // whose T is only a class) through IIntoChatOS.Get.
    public static T Get<T>(this IIntoChatOS brain, NeuronId id) where T : class
    {
        ArgumentNullException.ThrowIfNull(brain);
        if (typeof(INeuron).IsAssignableFrom(typeof(T))) { return (T)LegacyGet.MakeGenericMethod(typeof(T)).Invoke(brain, [id])!; }
        if (!PlainNeuronContracts.Is(typeof(T))) { throw new ArgumentException($"{typeof(T).Name} is neither a neuron contract nor a plain neuron contract.", nameof(T)); }
        return (T)Plain(brain).GetPlain(typeof(T), id);
    }

    // IIntoChatOS.Neuron for a plain contract (06-04): "{brain}/{relative}" in the client's brain.
    /// <exception cref="InvalidOperationException">This brain is not known.</exception>
    public static T Neuron<T>(this IIntoChatOS brain, NeuronId relative) where T : class
    {
        ArgumentNullException.ThrowIfNull(brain);
        var scope = Plain(brain).RelativeBrain
            ?? (brain.Self is Orleans.Runtime.IAddressable self ? self.GetGrainId().Key.ToString() : null)
            ?? throw new InvalidOperationException("This brain is not known.");
        return Get<T>(brain, relative.Within(scope));
    }

    public static IAsyncEnumerable<T> On<T>(this IIntoChatOS brain, object source, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => On<T>(brain, source, static () => Task.CompletedTask, cancellationToken);

    // onSubscribed runs once the synapse is registered, before the first signal can be missed.
    public static IAsyncEnumerable<T> On<T>(this IIntoChatOS brain, object source, Func<Task> onSubscribed, CancellationToken cancellationToken = default)
        where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onSubscribed);
        return Plain(brain).OnPlain<T>(source, onSubscribed, cancellationToken);
    }

    // One stream over a set of plain neurons (Sources.Named with plain members), following the set's registry.
    public static IAsyncEnumerable<T> On<T>(this IIntoChatOS brain, Sources sources, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(sources);
        var plain = Plain(brain);
        return SourceSet.Open<T>(brain, sources, (_, member, subscribed, stop) => plain.OnPlain<T>(member, subscribed, stop), cancellationToken);
    }

    // React<T> for plain neurons (06-04): rebuild once when registered, then once per [Signal] record, coalesced.
    public static Task React<T>(this IIntoChatOS brain, object source, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default)
        where T : DigitalBrain.Signal
        => React<T>(brain, Sources.Of([source]), rebuild, cancellationToken);

    public static Task React<T>(this IIntoChatOS brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken = default)
        where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Reaction.RunPlain<T>(brain, sources, rebuild, cancellationToken);
    }

    private static IPlainClient Plain(IIntoChatOS brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain as IPlainClient ?? throw new NotSupportedException($"{brain.GetType().Name} does not reach plain neurons.");
    }
}
