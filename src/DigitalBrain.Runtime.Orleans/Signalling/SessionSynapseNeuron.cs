using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Synapses;
using DigitalBrain.Spi;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Runtime.Orleans.Signalling;

/// <summary>
/// The per-session synapse neuron (04 D-02, D-13, R4-03): a plain neuron on <c>neuron.host</c> that is itself the synapse owner. The
/// engine hands each signal to <see cref="Deliver"/>, which queues it; <see cref="Next"/> cuts batches of at most
/// <see cref="SessionSynapseOptions.MaxBatchSignals"/> and sends the last one again until it is acknowledged. A timer checks
/// <see cref="SessionSynapseOptions.SessionTtl"/> after the last ack and then removes every synapse and cursor (RESEARCH Pitfall 3).
/// </summary>
/// <remarks>Queue changes happen only between awaits on the activation's scheduler; the long-poll completes through <see cref="_wake"/>.</remarks>
[Neuron(SessionSynapses.NeuronType)]
internal sealed class SessionSynapseNeuron(IOptions<SessionSynapseOptions> options, RuntimeClock clock, JournalPayloads payloads)
    : Neuron, ISessionSynapses, ISignalSink, IRemindedNeuron
{
    internal const string SweepTimer = "session.ttl";

    private readonly SessionSynapseOptions _options = options.Value;
    private readonly List<SessionSignal> _pending = [];
    private SessionBatch? _outstanding;
    private long _issued;
    private DateTimeOffset _lastAck;
    private TaskCompletionSource? _wake;

    private HostNeuronContext Host => (HostNeuronContext)Context;

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        _lastAck = clock.Now;
        Context.Timers.Start(SweepTimer, _options.SessionSweepEvery, _options.SessionSweepEvery);
        return Task.CompletedTask;
    }

    public async Task Subscribe(NeuronId source, string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var key = source.ToString();
        var held = Host.Engine.Sources;
        if (held.Count >= _options.MaxSessionSynapses && !held.Any(entry => entry.Source == key))
        {
            throw new SessionLimitException($"Session {Id} holds {held.Count} synapses, the most a session may ({_options.MaxSessionSynapses}).");
        }
        await Host.Engine.EnsureAliases(source, aliases).ConfigureAwait(true);
    }

    public Task Unsubscribe(NeuronId source) => Host.Engine.Remove(source);

    public async Task<SessionBatch?> Next(long acked, CancellationToken cancellationToken)
    {
        _lastAck = clock.Now;
        if (_outstanding is { } sent)
        {
            if (acked < sent.Number) { return sent; } // not acknowledged: send it again
            _outstanding = null;
        }
        var until = DateTime.UtcNow + _options.NextTimeout;
        while (_pending.Count == 0)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) { return null; }
            _wake ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(left);
            try { await _wake.Task.WaitAsync(limit.Token).ConfigureAwait(true); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            if (_outstanding is { } built) { return built; } // an interleaved Next cut the batch meanwhile
        }
        var take = _pending.Take(_options.MaxBatchSignals).ToArray();
        _pending.RemoveRange(0, take.Length);
        _outstanding = new SessionBatch(++_issued, take);
        return _outstanding;
    }

    public async Task End()
    {
        await Host.Engine.RemoveAll().ConfigureAwait(true);
        _pending.Clear();
        _outstanding = null;
        Wake();
        Context.Timers.Stop(SweepTimer);
        Host.DeactivateOnIdle();
    }

    public Task Deliver(JournaledSignal item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_pending.Count >= _options.MaxPendingSignals)
        {
            throw new SessionLimitException($"Session {Id} has {_pending.Count} signals waiting for Next; delivery resumes after an ack.");
        }
        var encoded = payloads.EncodeSignal(item.Signal);
        using var json = JsonDocument.Parse(encoded.Json);
        _pending.Add(new SessionSignal(item.Publisher.ToString(), item.Sequence, encoded.Type, json.RootElement.Clone(), item.At));
        Wake();
        return Task.CompletedTask;
    }

    public async Task OnReminder(string name, CancellationToken cancellationToken)
    {
        if (name != SweepTimer || clock.Now - _lastAck < _options.SessionTtl) { return; }
        await End().ConfigureAwait(true);
    }

    private void Wake()
    {
        var wake = _wake;
        _wake = null;
        wake?.TrySetResult();
    }
}
