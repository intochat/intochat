namespace IntoChat.OS;

// A set of neurons for IIntoChatOS.On<T>(Sources) and React<T>(Sources, …). Fixed, or named by
// one neuron: Select is asked on subscribe and again whenever Registry publishes TChange, so the
// set follows that neuron's state. Select is an ordinary neuron call and may read the registry.
public abstract class Sources
{
    private protected Sources() { }

    public static Sources Of(params INeuron[] neurons)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        return new Fixed([.. neurons]);
    }

    // Transitional (06-04, folded back in 06-08): a fixed set of plain neurons (proxies from Get), for On<T>/React<T> of their
    // [Signal] records.
    public static Sources Of(params object[] neurons)
    {
        ArgumentNullException.ThrowIfNull(neurons);
        return new Fixed(neurons);
    }

    // The registry is a grain neuron publishing an os Signal or (06-04) a plain neuron publishing a [Signal] record, such as a
    // migrated IText and its TextChanged; TChange decides which when the set subscribes.
    public static Sources Named<TRegistry, TChange>(TRegistry registry, Func<TRegistry, CancellationToken, Task<INeuron[]>> select)
        where TRegistry : class where TChange : class
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(select);
        return new Registry<TRegistry, TChange>(registry, async (named, ct) => await select(named, ct).ConfigureAwait(false));
    }

    // Transitional (06-07, folded back into Named in 06-08): a set whose members are plain neurons (contracts that are not
    // INeuron, such as the Time timer and reminder), for On<T> of their [Signal] records through PlainNeuronClient.
    public static Sources Named<TRegistry, TChange, TMember>(TRegistry registry, Func<TRegistry, CancellationToken, Task<TMember[]>> select)
        where TRegistry : class where TChange : class where TMember : class
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(select);
        return new Registry<TRegistry, TChange>(registry, async (named, ct) => [.. await select(named, ct).ConfigureAwait(false)]);
    }

    // The entry points behind IIntoChatOS.On<T>(Sources) and React<T>: one body in the vocabulary,
    // reached from the interface defaults and from IntoChatOSConnection alike.
    public static IAsyncEnumerable<T> Stream<T>(IIntoChatOS brain, Sources sources, CancellationToken cancellationToken) where T : Signal
        => SourceSet.Open<T>(brain, sources, null, coalesce: false, cancellationToken);

    // The same stream, observed: subscribed runs once every first member's subscription is registered (before the
    // first read can miss a signal), reselected after each later re-selection once joiners are subscribed and leavers
    // released. A caller that must not race the set's membership waits on these instead of a pause.
    public static IAsyncEnumerable<T> Stream<T>(IIntoChatOS brain, Sources sources, Func<Task>? subscribed, Action? reselected,
        CancellationToken cancellationToken) where T : Signal
        => SourceSet.Open<T>(brain, sources, subscribed, coalesce: false, cancellationToken, reselected);

    public static Task React<T>(IIntoChatOS brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken) where T : Signal
        => Reaction.Run<T>(brain, sources, rebuild, cancellationToken);

    internal abstract Task<object[]> SelectAsync(CancellationToken cancellationToken);

    // Every element means "select again". onSubscribed runs once the change subscription is registered,
    // so a select made there cannot miss a change. Empty for a fixed set; the merge never opens it then.
    internal abstract IAsyncEnumerable<object> Changes(IIntoChatOS brain, Func<Task> onSubscribed, CancellationToken cancellationToken);

    internal bool IsFixed => this is Fixed;

    private sealed class Fixed(object[] neurons) : Sources
    {
        internal override Task<object[]> SelectAsync(CancellationToken cancellationToken) => Task.FromResult<object[]>([.. neurons]);
        internal override IAsyncEnumerable<object> Changes(IIntoChatOS brain, Func<Task> onSubscribed, CancellationToken cancellationToken) => Empty();
        private static async IAsyncEnumerable<object> Empty() { await Task.CompletedTask; yield break; }
    }

    private sealed class Registry<TRegistry, TChange>(TRegistry registry, Func<TRegistry, CancellationToken, Task<object[]>> select) : Sources
        where TRegistry : class where TChange : class
    {
        private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        private static readonly System.Reflection.MethodInfo Legacy = typeof(Registry<TRegistry, TChange>).GetMethod(nameof(LegacyChanges), Private)!;
        private static readonly System.Reflection.MethodInfo Plain = typeof(Registry<TRegistry, TChange>).GetMethod(nameof(PlainChanges), Private)!;

        internal override Task<object[]> SelectAsync(CancellationToken cancellationToken) => select(registry, cancellationToken);

        internal override IAsyncEnumerable<object> Changes(IIntoChatOS brain, Func<Task> onSubscribed, CancellationToken cancellationToken)
        {
            var open = typeof(Signal).IsAssignableFrom(typeof(TChange)) && registry is INeuron ? Legacy
                : typeof(DigitalBrain.Signal).IsAssignableFrom(typeof(TChange)) ? Plain
                : throw new ArgumentException($"{typeof(TChange).Name} is neither a Signal of a grain registry nor a [Signal] record of a plain one.");
            return (IAsyncEnumerable<object>)open.MakeGenericMethod(typeof(TChange)).Invoke(null, [brain, registry, onSubscribed, cancellationToken])!;
        }

        private static async IAsyncEnumerable<object> LegacyChanges<T>(IIntoChatOS brain, TRegistry registry, Func<Task> onSubscribed,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) where T : Signal
        {
            await foreach (var change in brain.On<T>((INeuron)registry, onSubscribed, cancellationToken).ConfigureAwait(false)) { yield return change; }
        }

        private static async IAsyncEnumerable<object> PlainChanges<T>(IIntoChatOS brain, TRegistry registry, Func<Task> onSubscribed,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) where T : DigitalBrain.Signal
        {
            await foreach (var change in PlainNeuronClient.On<T>(brain, registry, onSubscribed, cancellationToken).ConfigureAwait(false)) { yield return change; }
        }
    }
}
