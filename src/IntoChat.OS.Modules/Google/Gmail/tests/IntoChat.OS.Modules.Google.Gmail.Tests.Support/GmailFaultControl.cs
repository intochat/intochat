using System.Collections.Concurrent;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Modules.Google.Gmail.Testing;

// Faults the real stores at persistence boundaries; transport and kernel composition remain unchanged.
public sealed class GmailFaultControl
{
    private readonly ConcurrentDictionary<string, GmailState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedMail> _mail = new();
    public bool FailNextDraftIntent { get; set; }
    public bool FailNextGrant { get; set; }
    public bool LoseNextGrantAcknowledgement { get; set; }
    public bool FailNextDraftCompletion { get; set; }
    public bool FailNextHistoryWork { get; set; }
    public bool FailNextHistoryCommit { get; set; }
    public bool FailNextWakeupClear { get; set; }
    public bool FailNextMailAppend { get; set; }
    public bool LoseNextMailAppendAcknowledgement { get; set; }
    public bool FailNextAccountingCompletion { get; set; }
    // A plain signal carries no trace (06-08): the fixture records the trace the publishing turn ran under.
    public sealed record RecordedMail(string EmailAddress, string HistoryId, Trace? Trace);
    public RecordedMail[] JournaledMail => [.. _mail];
    public object? SnapshotFor(string connectionKey) => _states.TryGetValue(connectionKey, out var state) ? state.Copy() : null;

    public void Configure(IServiceCollection services)
    {
        // The Gmail connection is a plain neuron (06-08): its writes reach the fixture through the connection write seam.
        services.AddSingleton<IConnectionWriteFaults>(new ConnectionFaults(this));
        var meter = services.Last(descriptor => descriptor.ServiceType == typeof(ICostMeter));
        services.Remove(meter);
        services.Add(new ServiceDescriptor(typeof(ICostMeter), provider => new FaultingMeter((ICostMeter)(meter.ImplementationInstance
            ?? meter.ImplementationFactory?.Invoke(provider) ?? ActivatorUtilities.CreateInstance(provider, meter.ImplementationType!)), this), meter.Lifetime));
    }

    private void BeforeWrite(string key, GmailState state)
    {
        _states.TryGetValue(key, out var previous);
        if (FailNextGrant && state.GrantGeneration != previous?.GrantGeneration)
        { FailNextGrant = false; throw new IOException("Fixture grant write failure."); }
        if (FailNextDraftIntent && state.DraftOperations.Any(operation => operation.Value.Status == GmailDraftOperationStatus.Pending
            && previous?.DraftOperations.Any(old => old.Value.RequestId == operation.Value.RequestId) != true))
        { FailNextDraftIntent = false; throw new IOException("Fixture draft intent write failure."); }
        if (FailNextDraftCompletion && previous?.DraftOperations.Any(old => old.Value.Status == GmailDraftOperationStatus.Pending
            && state.DraftOperations.Any(operation => operation.Value.RequestId == old.Value.RequestId && operation.Value.Status != GmailDraftOperationStatus.Pending)) == true)
        { FailNextDraftCompletion = false; throw new IOException("Fixture draft completion write failure."); }
        if (FailNextHistoryWork && state.History?.Work is not null)
        { FailNextHistoryWork = false; throw new IOException("Fixture history work write failure."); }
        if (FailNextHistoryCommit && state.History?.Wakeup is not null)
        { FailNextHistoryCommit = false; throw new IOException("Fixture history completion write failure."); }
        if (FailNextWakeupClear && previous?.History?.Wakeup is not null && state.History?.Wakeup is null)
        { FailNextWakeupClear = false; throw new IOException("Fixture wake-up clear write failure."); }
    }

    private sealed class ConnectionFaults(GmailFaultControl faults) : IConnectionWriteFaults
    {
        public void Loaded(string connectionKey, object state)
        { if (state is GmailState loaded) { faults._states[connectionKey] = loaded.Copy(); } }

        public Task BeforeWrite(string connectionKey, object? next, IReadOnlyList<DigitalBrain.Signal> signals)
        {
            if (next is GmailState state) { faults.BeforeWrite(connectionKey, state); }
            if (signals.OfType<MailReceived>().Any() && faults.FailNextMailAppend)
            { faults.FailNextMailAppend = false; throw new IOException("Fixture signal journal write failure."); }
            return Task.CompletedTask;
        }

        public Task AfterWrite(string connectionKey, object? next, IReadOnlyList<DigitalBrain.Signal> signals)
        {
            var grantChanged = next is GmailState candidate
                && faults._states.TryGetValue(connectionKey, out var previous)
                && candidate.GrantGeneration != previous.GrantGeneration;
            if (next is GmailState committed) { faults._states[connectionKey] = committed.Copy(); }
            var mail = signals.OfType<MailReceived>().ToArray();
            foreach (var signal in mail) { faults._mail.Enqueue(new(signal.EmailAddress, signal.HistoryId, IntoChat.OS.Framework.Compute.TraceContext.Current)); }
            if (grantChanged && faults.LoseNextGrantAcknowledgement)
            { faults.LoseNextGrantAcknowledgement = false; throw new IOException("Fixture grant acknowledgement lost."); }
            if (mail.Length > 0 && faults.LoseNextMailAppendAcknowledgement)
            { faults.LoseNextMailAppendAcknowledgement = false; throw new IOException("Fixture journal acknowledgement lost."); }
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingMeter(ICostMeter inner, GmailFaultControl faults) : ICostMeter
    {
        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
            => inner.RecordAsync(kind, item, quantity, unit, funding, costId, cancellationToken, subject);
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken)
            => inner.BeginExternalCallAsync(request, cancellationToken);
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken)
            => inner.MarkExternalCallDispatchStartedAsync(handle, cancellationToken);
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome,
            CancellationToken cancellationToken)
        {
            if (faults.FailNextAccountingCompletion)
            {
                faults.FailNextAccountingCompletion = false;
                return ValueTask.FromException<ExternalCallCompletion>(new IOException("Fixture accounting completion unavailable."));
            }
            return inner.CompleteExternalCallAsync(handle, outcome, cancellationToken);
        }
    }
}
