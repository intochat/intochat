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

    // Recover after registration so invocations cannot fall between the snapshot and the watch.
    // Delivery across process restarts remains at least once; handlers must be idempotent.
    private static async IAsyncEnumerable<AppInvoked> Read(IApp app, IDigitalBrain brain, HashSet<string>? operations,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource<AppInvocation[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var live = brain.On<AppInvoked>(app, async () =>
        {
            var snapshot = await app.Pending();
            ready.TrySetResult(snapshot.Where(invocation => operations is null || operations.Contains(invocation.Operation)).ToArray());
        }, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        var next = live.MoveNextAsync().AsTask();
        try
        {
            await Task.WhenAny(ready.Task, next);
            if (!ready.Task.IsCompletedSuccessfully)
            {
                await next;
                throw new InvalidOperationException("The invocation stream ended before subscription registration.");
            }
            var pending = await ready.Task;
            var recovered = pending.Select(x => x.Id).ToHashSet();
            foreach (var invocation in pending)
            { yield return new(invocation.Id, invocation.Operation, invocation.Input); }
            while (await next)
            {
                var invoked = live.Current;
                next = live.MoveNextAsync().AsTask();
                // An unrelated operation cannot affect this handler, so it needs no durable-state read.
                if (operations is not null && !operations.Contains(invoked.Operation)) { continue; }
                if (recovered.Remove(invoked.InvocationId)) { continue; }
                // Completed history may already be pruned. Pending is also safe over the script
                // HTTP edge, which does not preserve the server's exception types.
                if ((await app.Pending()).Any(current => current.Id == invoked.InvocationId))
                { yield return invoked; }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            // A compiler-generated iterator cannot be disposed while MoveNext is pending.
            try { await next; } catch (Exception) { }
        }
    }
}
