using System.Runtime.CompilerServices;
using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps;

// An app's operations arrive as AppRequested; a synapse for one operation watches only that one.
public static class AppSubscriptions
{
    public static IAsyncEnumerable<AppRequested> On(this IDigitalBrain brain, IApp app, string operation, CancellationToken cancellationToken = default)
        => On(brain, app, operation, static () => Task.CompletedTask, cancellationToken);

    // onSubscribed runs once the subscription is registered: requests made after it are delivered.
    public static async IAsyncEnumerable<AppRequested> On(this IDigitalBrain brain, IApp app, string operation, Func<Task> onSubscribed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await foreach (var request in brain.On<AppRequested>(app, onSubscribed, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(request.Operation, operation, StringComparison.Ordinal)) { yield return request; }
        }
    }
}
