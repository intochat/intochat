using System.Runtime.CompilerServices;
using DigitalBrain.Apps.Signals;

namespace DigitalBrain.Apps;

// An app's operations arrive as AppRequested; a synapse for one operation watches only that one.
public static class AppSubscriptions
{
    public static async IAsyncEnumerable<AppRequested> On(this IDigitalBrain brain, IApp app, string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await foreach (var request in brain.On<AppRequested>(app, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(request.Operation, operation, StringComparison.Ordinal)) { yield return request; }
        }
    }
}
