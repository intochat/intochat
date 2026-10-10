namespace DigitalBrain;

// A set of neurons for IDigitalBrain.On<T>(Sources) and React<T>(Sources, …). Fixed, or named by
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

    // The entry points behind IDigitalBrain.On<T>(Sources) and React<T>: one body in the vocabulary,
    // reached from the interface defaults and from DigitalBrainConnection alike.
    public static IAsyncEnumerable<T> Stream<T>(IDigitalBrain brain, Sources sources, CancellationToken cancellationToken) where T : Signal
        => SourceSet.Open<T>(brain, sources, null, coalesce: false, cancellationToken);

    public static Task React<T>(IDigitalBrain brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken) where T : Signal
        => throw new NotImplementedException();

    internal abstract Task<INeuron[]> SelectAsync(CancellationToken cancellationToken);

    // Every element means "select again". Empty for a fixed set; the merge skips the pump then.
    internal abstract IAsyncEnumerable<Signal> Changes(IDigitalBrain brain, CancellationToken cancellationToken);

    internal bool IsFixed => this is Fixed;

    private sealed class Fixed(INeuron[] neurons) : Sources
    {
        internal override Task<INeuron[]> SelectAsync(CancellationToken cancellationToken) => Task.FromResult(neurons);
        internal override IAsyncEnumerable<Signal> Changes(IDigitalBrain brain, CancellationToken cancellationToken) => Empty();
        private static async IAsyncEnumerable<Signal> Empty() { await Task.CompletedTask; yield break; }
    }

    private sealed class Registry<TRegistry, TChange>(TRegistry registry, Func<TRegistry, CancellationToken, Task<INeuron[]>> select) : Sources
        where TRegistry : INeuron where TChange : Signal
    {
        internal override Task<INeuron[]> SelectAsync(CancellationToken cancellationToken) => select(registry, cancellationToken);
        internal override async IAsyncEnumerable<Signal> Changes(IDigitalBrain brain, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var change in brain.On<TChange>(registry, cancellationToken).ConfigureAwait(false)) { yield return change; }
        }
    }
}
