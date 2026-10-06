using System.Runtime.CompilerServices;
using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps;

public static class AppInvocations
{
    // Start watching before recovery: priming the enumerator opens the stream, so the brain is
    // registering the durable subscription while Pending is read. The watch cannot be awaited
    // separately from its first signal, so an invocation that lands in that short window is
    // picked up by the Pending re-check on the next signal rather than lost.
    // Delivery across process restarts remains at least once; handlers must be idempotent.
    public static async IAsyncEnumerable<AppInvoked> Invocations(this IApp app, IDigitalBrain brain,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var live = brain.On<AppInvoked>(app, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = live.MoveNextAsync();
        var pending = await app.Pending();
        var recovered = pending.Select(x => x.Id).ToHashSet();
        foreach (var invocation in pending)
        { yield return new(invocation.Id, invocation.Operation, invocation.Input); }
        while (await next)
        {
            var invoked = live.Current;
            next = live.MoveNextAsync();
            if (recovered.Remove(invoked.InvocationId)) { continue; }
            // Completed history may already be pruned. Pending is also safe over the script
            // HTTP edge, which does not preserve the server's exception types.
            if ((await app.Pending()).Any(current => current.Id == invoked.InvocationId))
            { yield return invoked; }
        }
    }
}
