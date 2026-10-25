namespace IntoChat.OS;

// React<T>: rebuild once when registered, then once per signal. The set stream is opened with
// coalesce, so a burst queued behind one signal is one rebuild: the handler reads authoritative
// state, and one run after a burst is both correct and cheaper.
internal static class Reaction
{
    public static async Task Run<T>(IIntoChatOS brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        try
        {
            await foreach (var _ in SourceSet.Open<T>(brain, sources, () => rebuild(cancellationToken), coalesce: true, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            { await rebuild(cancellationToken).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // The same for plain neurons' [Signal] records (06-04, folded back in 06-08): members subscribe through PlainNeuronClient.
    public static async Task RunPlain<T>(IIntoChatOS brain, Sources sources, Func<CancellationToken, Task> rebuild, CancellationToken cancellationToken)
        where T : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        try
        {
            var set = SourceSet.Open<T>(brain, sources, OpenPlain<T>, cancellationToken, () => rebuild(cancellationToken), coalesce: true);
            await foreach (var _ in set.WithCancellation(cancellationToken).ConfigureAwait(false)) { await rebuild(cancellationToken).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // One member's subscription in a plain set: the session synapse subscription PlainNeuronClient opens.
    private static IAsyncEnumerable<T> OpenPlain<T>(IIntoChatOS brain, object member, Func<Task> subscribed, CancellationToken stop) where T : DigitalBrain.Signal
        => PlainNeuronClient.On<T>(brain, member, subscribed, stop);
}
