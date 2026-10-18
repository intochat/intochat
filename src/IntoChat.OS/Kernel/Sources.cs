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
        return new Fixed(neurons);
    }

    public static Sources Named<TRegistry, TChange>(TRegistry registry, Func<TRegistry, CancellationToken, Task<INeuron[]>> select)
        where TRegistry : INeuron where TChange : Signal
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(select);
        return new Registry<TRegistry, TChange>(registry, select);
    }

    // The entry points behind IIntoChatOS.On<T>(Sources) and React<T>: one body in the vocabulary,
    // reached from the interface defaults and from IntoChatOSConnection alike.
    public static IAsyncEnumerable<T> Stream<T>(IIntoChatOS brain, Sources sources, CancellationToken cancellationToken) where T : Signal
        => SourceSet.Open<T>(brain, sources, null, coalesce: false, cancellationToken);

    public static Task React<T>(IIntoChatOS brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken) where T : Signal
        => Reaction.Run<T>(brain, sources, rebuild, cancellationToken);

    internal abstract Task<INeuron[]> SelectAsync(CancellationToken cancellationToken);

    // Every element means "select again". onSubscribed runs once the change subscription is registered,
    // so a select made there cannot miss a change. Empty for a fixed set; the merge never opens it then.
    internal abstract IAsyncEnumerable<Signal> Changes(IIntoChatOS brain, Func<Task> onSubscribed, CancellationToken cancellationToken);

    internal bool IsFixed => this is Fixed;

    private sealed class Fixed(INeuron[] neurons) : Sources
    {
        internal override Task<INeuron[]> SelectAsync(CancellationToken cancellationToken) => Task.FromResult(neurons);
        internal override IAsyncEnumerable<Signal> Changes(IIntoChatOS brain, Func<Task> onSubscribed, CancellationToken cancellationToken) => Empty();
        private static async IAsyncEnumerable<Signal> Empty() { await Task.CompletedTask; yield break; }
    }

    private sealed class Registry<TRegistry, TChange>(TRegistry registry, Func<TRegistry, CancellationToken, Task<INeuron[]>> select) : Sources
        where TRegistry : INeuron where TChange : Signal
    {
        internal override Task<INeuron[]> SelectAsync(CancellationToken cancellationToken) => select(registry, cancellationToken);
        internal override async IAsyncEnumerable<Signal> Changes(IIntoChatOS brain, Func<Task> onSubscribed,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var change in brain.On<TChange>(registry, onSubscribed, cancellationToken).ConfigureAwait(false)) { yield return change; }
        }
    }
}
