using System.Runtime.CompilerServices;
using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps;

public static class AppInvocations
{
    public static IAsyncEnumerable<AppInvoked> Invocations(this IApp app, IDigitalBrain brain,
        string[] operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return Read(app, brain, operations.ToHashSet(StringComparer.Ordinal), cancellationToken);
    }

    public static IAsyncEnumerable<AppInvoked> Invocations(this IApp app, IDigitalBrain brain,
        CancellationToken cancellationToken = default)
        => Read(app, brain, null, cancellationToken);

    // Start watching before recovery: priming the enumerator opens the stream, so the brain is
    // registering the durable subscription while Pending is read. The watch cannot be awaited
    // separately from its first signal, so an invocation that lands in that short window is
    // picked up by the Pending re-check on the next signal rather than lost.
    // Delivery across process restarts remains at least once; handlers must be idempotent.
    private static async IAsyncEnumerable<AppInvoked> Read(IApp app, IDigitalBrain brain, HashSet<string>? operations,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var live = brain.On<AppInvoked>(app, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = live.MoveNextAsync();
        var pending = (await app.Pending()).Where(invocation => operations is null || operations.Contains(invocation.Operation)).ToArray();
        var recovered = pending.Select(x => x.Id).ToHashSet();
        foreach (var invocation in pending)
        { yield return new(invocation.Id, invocation.Operation, invocation.Input); }
        while (await next)
        {
            var invoked = live.Current;
            next = live.MoveNextAsync();
            // An unrelated operation cannot affect this handler, so it needs no durable-state read.
            if (operations is not null && !operations.Contains(invoked.Operation)) { continue; }
            if (recovered.Remove(invoked.InvocationId)) { continue; }
            // Completed history may already be pruned. Pending is also safe over the script
            // HTTP edge, which does not preserve the server's exception types.
            if ((await app.Pending()).Any(current => current.Id == invoked.InvocationId))
            { yield return invoked; }
        }
    }
}
