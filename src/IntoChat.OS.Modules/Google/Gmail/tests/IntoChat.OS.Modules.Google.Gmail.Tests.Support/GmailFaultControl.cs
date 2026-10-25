using System.Collections.Concurrent;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Modules.Google.Gmail.Testing;

// Faults the real stores at persistence boundaries; transport and kernel composition remain unchanged.
public sealed class GmailFaultControl
{
    private readonly ConcurrentDictionary<string, GmailState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<MailReceived> _mail = new();
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
    public MailReceived[] JournaledMail => [.. _mail];
    public object? SnapshotFor(string connectionKey) => _states.TryGetValue(connectionKey, out var state) ? state.Copy() : null;

    public void Configure(IServiceCollection services)
    {
        var storage = services.Last(descriptor => descriptor.ServiceType == typeof(IGrainStorage)
            && Equals(descriptor.ServiceKey, IntoChatOSNames.LegacyClusterGrainStorage));
        services.Remove(storage);
        services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage, (provider, key) => new FaultingStorage(
            (IGrainStorage)(storage.KeyedImplementationInstance ?? storage.KeyedImplementationFactory?.Invoke(provider, key)
                ?? ActivatorUtilities.CreateInstance(provider, storage.KeyedImplementationType!)), this));
        var journal = services.Last(descriptor => descriptor.ServiceType == typeof(ISignalJournal));
        services.Remove(journal);
        services.AddSingleton<ISignalJournal>(provider => new FaultingJournal((ISignalJournal)(journal.ImplementationInstance
            ?? journal.ImplementationFactory?.Invoke(provider) ?? ActivatorUtilities.CreateInstance(provider, journal.ImplementationType!)), this));
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

    private sealed class FaultingStorage(IGrainStorage inner, GmailFaultControl faults) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            await inner.ReadStateAsync(stateName, grainId, grainState);
            if (grainState.State is GmailState state) { faults._states[grainId.Key.ToString()] = state.Copy(); }
        }
        public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var grantChanged = grainState.State is GmailState candidate
                && faults._states.TryGetValue(grainId.Key.ToString(), out var previous)
                && candidate.GrantGeneration != previous.GrantGeneration;
            if (grainState.State is GmailState state) { faults.BeforeWrite(grainId.Key.ToString(), state); }
            await inner.WriteStateAsync(stateName, grainId, grainState);
            if (grainState.State is GmailState committed) { faults._states[grainId.Key.ToString()] = committed.Copy(); }
            if (grantChanged && faults.LoseNextGrantAcknowledgement)
            { faults.LoseNextGrantAcknowledgement = false; throw new IOException("Fixture grant acknowledgement lost."); }
        }
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => inner.ClearStateAsync(stateName, grainId, grainState);
        public void Participate(ISiloLifecycle lifecycle)
        { if (inner is ILifecycleParticipant<ISiloLifecycle> participant) { participant.Participate(lifecycle); } }
    }

    private sealed class FaultingJournal(ISignalJournal inner, GmailFaultControl faults) : ISignalJournal
    {
        public async Task<long> Append(GrainId publisher, Signal[] signals)
        {
            var mail = signals.OfType<MailReceived>().ToArray();
            if (mail.Length > 0 && faults.FailNextMailAppend)
            { faults.FailNextMailAppend = false; throw new IOException("Fixture signal journal write failure."); }
            var sequence = await inner.Append(publisher, signals);
            foreach (var signal in mail) { faults._mail.Enqueue(signal); }
            if (mail.Length > 0 && faults.LoseNextMailAppendAcknowledgement)
            { faults.LoseNextMailAppendAcknowledgement = false; throw new IOException("Fixture journal acknowledgement lost."); }
            return sequence;
        }
        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => inner.ReadSince(brainId, since, limit);
        public Task<PartSignals?> SignalsOf(GrainId publisher, long since) => inner.SignalsOf(publisher, since);
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
