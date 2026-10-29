using System.Runtime.CompilerServices;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps;

// An app's operations arrive as AppRequested; a synapse for one operation watches only that one.
public static class AppSubscriptions
{
    public static IAsyncEnumerable<AppRequested> On(this ISignalWatch brain, IApp app, string operation, CancellationToken cancellationToken = default)
        => On(brain, app, operation, static () => Task.CompletedTask, cancellationToken);

    // onSubscribed runs once the subscription is registered: requests made after it are delivered.
    public static async IAsyncEnumerable<AppRequested> On(this ISignalWatch brain, IApp app, string operation, Func<Task> onSubscribed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        // The selector asks the runtime not to deliver other operations. A delivery that still
        // arrives is passed over, so its batch is acknowledged as skipped rather than as a run.
        var requests = brain is ISelectiveWatch selective
            ? selective.On<AppRequested>(app, operation, onSubscribed, cancellationToken)
            : brain.On<AppRequested>(app, onSubscribed, cancellationToken);
        await foreach (var request in requests.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(request.Operation, operation, StringComparison.Ordinal)) { yield return request; }
            else { SignalDeliveries.Skip(request); }
        }
    }
}
