using IntoChat.OS.Modules.Microsoft.Orleans;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition.Cluster;

// Orleans already owns one membership-update pump per silo. Enumerating its multicast
// stream adds no management RPC, timer, or global registry of grain references.
internal sealed class ClusterMembershipFeed(IClusterMembershipService membership, ILogger<ClusterMembershipFeed> logger) : IClusterMembershipFeed
{
    public ClusterSnapshot Current => Project(membership.CurrentSnapshot);

    public IAsyncDisposable Subscribe(Func<ClusterSnapshot?, Task> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new Subscription(membership, changed, logger);
    }

    private static ClusterSnapshot Project(ClusterMembershipSnapshot snapshot)
        => new([.. snapshot.Members.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => new SiloView(pair.Key.ToString(), pair.Key.Endpoint.ToString(), pair.Value.Status.ToString(), null, ThisAssembly.Sha))],
            snapshot.Version.Value);

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly Task pump;

        public Subscription(IClusterMembershipService membership, Func<ClusterSnapshot?, Task> changed, ILogger logger)
        {
            // A background callback must not inherit the user/trace that activated its grain.
            using (ExecutionContext.SuppressFlow()) { pump = Task.Run(Run); }

            async Task Run()
            {
                var unavailable = false;
                ClusterSnapshot? previous = null;
                while (!stopping.IsCancellationRequested)
                {
                    try
                    {
                        await foreach (var snapshot in membership.MembershipUpdates.WithCancellation(stopping.Token))
                        {
                            var next = Project(snapshot);
                            if (!unavailable && previous is not null && previous.Silos.SequenceEqual(next.Silos)) { continue; }
                            await changed(next).WaitAsync(stopping.Token);
                            previous = next;
                            unavailable = false;
                        }
                        stopping.Token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("Orleans membership updates ended before shutdown.");
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        if (!unavailable)
                        {
                            logger.LogWarning(error, "Cluster membership updates could not be delivered; resubscribing.");
                            unavailable = true;
                            try { await changed(null).WaitAsync(stopping.Token); }
                            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                            catch (Exception notification) { logger.LogWarning(notification, "Cluster membership failure notification failed."); }
                        }
                        try { await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token); }
                        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            // Cancelling WaitAsync also releases a callback queued behind deactivation.
            await pump;
            stopping.Dispose();
        }
    }
}
