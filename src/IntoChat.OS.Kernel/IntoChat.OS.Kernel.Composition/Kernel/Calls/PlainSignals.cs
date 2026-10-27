using System.Text;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Signalling;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-07, deleted in 06-08): the os clients reach the runtime's session synapse neuron (04-13) over the same sink
// the plain-contract seam uses, and read plain publishers from the shared BrainLog. Sessions carry no neuron caller: the
// session key is the subscriber the source's AdmitSynapse judges.
internal sealed class PlainSignals(IGrainFactory grains, IServiceProvider services) : IPlainSignals
{
    public IPlainSession Session(string brain, string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        KernelInvocationSink.Register(typeof(ISessionSynapses));
        return new SessionChannel(NeuronProxy.Create<ISessionSynapses>(SessionSynapses.For(new global::DigitalBrain.BrainId(brain), session),
            new KernelInvocationSink(grains, string.Empty)));
    }

    public async Task<PlainSignalPage> Read(global::DigitalBrain.NeuronId publisher, long since, int limit, CancellationToken cancellationToken)
    {
        // Resolved on use: a host without the runtime's BrainLog (the memory journal of light test hosts) has no plain publishers.
        var journal = services.GetService(typeof(IBrainJournalWriter)) as IBrainJournalWriter
            ?? throw new InvalidOperationException("This host has no BrainLog to read plain publishers from.");
        var page = await journal.ReadSince(publisher.Brain.Value, publisher.ToString(), since, limit, cancellationToken).ConfigureAwait(false);
        return new(page.Floor, page.Last, [.. page.Signals.Select(entry =>
            new PlainSignalEntry(entry.Publisher, entry.Sequence, entry.Type, Encoding.UTF8.GetString(entry.Json.Span), entry.At))]);
    }

    private sealed class SessionChannel(ISessionSynapses session) : IPlainSession
    {
        public Task Subscribe(global::DigitalBrain.NeuronId source, string[] aliases) => session.Subscribe(source, aliases);

        public Task Unsubscribe(global::DigitalBrain.NeuronId source) => session.Unsubscribe(source);

        // A poll whose answer timed out reads as an empty one: the caller polls again with the same ack and the session sends the
        // batch it cut into the lost answer again, so a slow silo delays signals instead of ending the subscription.
        public async Task<PlainSignalBatch?> Next(long acked, CancellationToken cancellationToken)
        {
            SessionBatch? batch;
            try { batch = await session.Next(acked, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested) { return null; }
            return batch is null ? null : new PlainSignalBatch(batch.Number, [.. batch.Signals.Select(signal =>
                new PlainSignalEntry(signal.Source, signal.Sequence, signal.Alias, signal.Payload.GetRawText(), signal.At))]);
        }

        public Task End() => session.End();
    }
}
