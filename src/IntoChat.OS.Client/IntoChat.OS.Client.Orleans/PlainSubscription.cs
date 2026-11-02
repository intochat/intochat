using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Signalling;
using Microsoft.Extensions.Logging;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Client.Orleans;

// In-process brain.On<T> of a plain neuron (06-07, R4-15): one session on the runtime's session synapse neuron (04-13) per
// subscription. The synapse is registered before onSubscribed runs; a batch is acknowledged by the next Next only once the
// consumer has taken every signal of it, so an unacknowledged batch is resent. A gap is reported as SubscriptionMissedSignals
// and the session reads on from the floor; disposal ends the session, which removes its synapses (an abandoned one expires
// after the runtime's SessionTtl).
internal static class PlainSubscription
{
    public static IAsyncEnumerable<T> Open<T>(IServiceProvider services, object source, ILogger logger, Func<Task> onSubscribed, CancellationToken cancellationToken)
        where T : DigitalBrain.Signal
    {
        var target = source is DigitalBrain.Invocation.NeuronProxy proxy
            ? proxy.Target
            : throw new ArgumentException("Subscribe to a plain neuron obtained from this brain's Get.", nameof(source));
        var alias = SignalLog.Alias(typeof(T));
        return new WatchStream<T>(async ct =>
        {
            var session = SiloNeurons.Get<ISessionSynapses>(services, SessionSynapses.For(target.Brain, "client/" + Guid.NewGuid().ToString("N")));
            try { await session.Subscribe(target, [alias]).WaitAsync(ct).ConfigureAwait(false); }
            catch { await EndAsync(session, logger, target).ConfigureAwait(false); throw; }
            logger.LogInformation("SubscriptionReady {Source} {SignalType}", target, typeof(T).Name);
            return (Read<T>(session, target, alias, logger, ct), new Ending(session, logger, target));
        }, cancellationToken, default, onSubscribed);
    }

    private static async IAsyncEnumerable<T> Read<T>(ISessionSynapses session, DigitalBrain.NeuronId target, string alias, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken) where T : DigitalBrain.Signal
    {
        long acked = 0;
        while (true)
        {
            var batch = await session.Next(acked, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null) { continue; }
            foreach (var entry in batch.Signals)
            {
                if (SignalLog.IsGap(entry)) { logger.LogWarning("SubscriptionMissedSignals {Source} {SignalType} {Gap}", target, typeof(T).Name, entry.Payload.GetRawText()); continue; }
                if (entry.Alias == alias) { yield return JsonSerializer.Deserialize<T>(entry.Payload, JsonSerializerOptions.Web)!; }
            }
            // Resuming here means the consumer asked for more: every signal of the batch was taken.
            acked = batch.Number;
        }
    }

    private static async Task EndAsync(ISessionSynapses session, ILogger logger, DigitalBrain.NeuronId target)
    {
        try { await session.End().ConfigureAwait(false); }
        catch (Exception error) { logger.LogWarning(error, "Subscription cleanup failed for {Source}; the session expires after its SessionTtl.", target); }
    }

    private sealed class Ending(ISessionSynapses session, ILogger logger, DigitalBrain.NeuronId target) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await EndAsync(session, logger, target).ConfigureAwait(false);
    }
}
