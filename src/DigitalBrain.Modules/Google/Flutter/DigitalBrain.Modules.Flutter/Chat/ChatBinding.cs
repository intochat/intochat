using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Flutter.Chat;

internal sealed partial class ChatNeuron
{
    private INeuron? _source;
    private Guid _sourceActivation;
    private IGrainTimer? _renewal;
    private bool _dirty;
    private bool _queued;
    private bool _watchReady;
    private IChatAgentSource Adapter => ServiceProvider.GetRequiredService<IChatAgentSource>();

    public static bool IsNudge(IInvokable request) => request.GetInterfaceType() == typeof(INeuronObserver);

    public async Task<ChatSelection> SelectAgent(string agent, long expectedSelectionRevision, Guid operationId)
    {
        ValidateSelection(agent, operationId);
        if (Snapshot.SelectionReceipts.FirstOrDefault(receipt => receipt.OperationId == operationId) is { } receipt)
        {
            if (receipt.Selection.Agent != agent) { throw new InvalidOperationException("This operation selected a different agent."); }
            await Recover();
            return receipt.Selection;
        }
        var revision = Snapshot.Selection?.Revision ?? 0;
        if (revision != expectedSelectionRevision) { throw new RevisionConflictException("The selected conversation changed.", revision); }
        var selected = Snapshot.Selection?.Agent == agent ? Snapshot.Selection : new ChatSelection(agent, checked(revision + 1));
        await CommitSelection(selected, operationId);
        await Recover();
        return selected;
    }

    public async Task AdoptAgent(string agent, long generation, Guid operationId)
    {
        ValidateSelection(agent, operationId);
        if (Snapshot.SelectionReceipts.FirstOrDefault(receipt => receipt.OperationId == operationId) is { } receipt)
        {
            if (receipt.Selection.Agent != agent || receipt.Selection.Revision != generation)
            { throw new InvalidOperationException("This migration operation adopted a different selection."); }
            await Recover();
            return;
        }
        if (Snapshot.Selection is { } selected)
        {
            if (selected.Agent != agent || selected.Revision != generation) { throw new InvalidOperationException("This chat has already been bound to a different selection."); }
            await CommitSelection(selected, operationId);
            await Recover();
            return;
        }
        if (generation < 0 || generation < (Snapshot.Projection?.Generation ?? 0)
            || (Snapshot.Projection is { } projection && projection.Source != agent && generation <= projection.Generation))
        { throw new InvalidOperationException("Migration must retain the current chat source and generation."); }
        await CommitSelection(new(agent, generation), operationId);
        await Recover();
    }

    private void ValidateSelection(string agent, Guid operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        if (agent.Length > 512 || operationId == Guid.Empty) { throw new ArgumentException("Selection needs a bounded source and an operation ID."); }
        if (!BrainKey.BelongsTo(agent, BrainKey.BrainOf(this.GetPrimaryKeyString())))
        { throw new UnauthorizedAccessException("A chat can only select an agent in its own brain."); }
        _ = Adapter;
    }

    private async Task CommitSelection(ChatSelection selected, Guid operationId)
    {
        // Authorize/resolve the source before persisting a binding. This also rejects unavailable adapters.
        await Adapter.Read(selected.Agent, selected.Revision);
        var previous = Snapshot;
        var changed = previous.Selection != selected;
        var clearRun = changed && previous.Run is { } run
            && (run.Source != selected.Agent || run.SelectionRevision != selected.Revision);
        var next = previous with
        {
            Name = this.GetPrimaryKeyString(),
            Selection = selected,
            SelectionReceipts = [.. previous.SelectionReceipts, new(operationId, selected)],
            // Do not expose the old conversation/actions while the new projection is being recovered.
            Projection = changed && previous.Projection is { } projection
                && (projection.Source != selected.Agent || projection.Generation != selected.Revision) ? null : previous.Projection,
            Run = clearRun ? null : previous.Run,
            ActivityRevision = clearRun ? checked(previous.ActivityRevision + 1) : previous.ActivityRevision,
            Revision = changed ? checked(previous.Revision + 1) : previous.Revision,
        };
        if (changed)
        {
            var selectionSignal = new ChatSelectionChanged(this.GetPrimaryKeyString(), selected);
            var changedSignal = new ChatChanged(this.GetPrimaryKeyString(), next.Revision);
            await Save(next, clearRun
                ? [(selectionSignal, null), (changedSignal, null), (new ChatRunChanged(this.GetPrimaryKeyString(), next.ActivityRevision), null)]
                : [(selectionSignal, null), (changedSignal, null)]);
        }
        else { await SaveQuietly(next); }
    }

    internal async Task Recover()
    {
        if (Snapshot.Selection is not { } selected) { return; }
        var next = Adapter.Source(selected.Agent);
        if (!_watchReady || _source?.GetGrainId() != next.GetGrainId())
        {
            var old = _source;
            // Watch before reading; interleaved nudges cannot mutate durable state.
            _source = next;
            _watchReady = false;
            _sourceActivation = await next.Watch(this.AsReference<INeuronObserver>());
            _watchReady = true;
            if (old is not null && old.GetGrainId() != next.GetGrainId()) { await old.Unwatch(this.AsReference<INeuronObserver>()); }
        }
        _dirty = true;
        _renewal ??= this.RegisterGrainTimer(_ => Renew(), new GrainTimerCreationOptions
        { DueTime = ObserverRenewal, Period = ObserverRenewal, KeepAlive = false, Interleave = false });
        await Reconcile();
    }

    Task INeuronObserver.OnSignalAsync(Signal signal)
    {
        if (_source is null || signal.Publisher.Name != _source.GetGrainId().ToString()) { return Task.CompletedTask; }
        _dirty = true;
        return QueueReconciliation();
    }

    private Task QueueReconciliation()
    {
        if (!_dirty || _queued) { return Task.CompletedTask; }
        _queued = true;
        return this.AsReference<IChatReconciliation>().Reconcile();
    }

    public async Task Reconcile()
    {
        _queued = false;
        if (!_watchReady && Snapshot.Selection is not null) { await Recover(); return; }
        if (!_dirty || Snapshot.Selection is not { } selected) { return; }
        _dirty = false;
        try
        {
            var snapshot = await Adapter.Read(selected.Agent, selected.Revision);
            if (Snapshot.Selection != selected) { return; }
            await ProjectCore(snapshot.Projection, snapshot.Run);
        }
        catch
        {
            // A failed snapshot never advances the successful watermark; the renewal recovers it.
            _dirty = true;
            throw;
        }
    }

    internal Task ActivateBinding()
    {
        if (Snapshot.Selection is null) { return Task.CompletedTask; }
        _dirty = true;
        return this.AsReference<IChatReconciliation>().Reconcile();
    }

    private async Task Renew()
    {
        try
        {
            if (_source is null) { await Recover(); return; }
            var activation = await _source.Watch(this.AsReference<INeuronObserver>());
            if (activation != _sourceActivation) { _sourceActivation = activation; _dirty = true; }
            await Reconcile();
        }
        catch (Exception error)
        {
            _dirty = true;
            ServiceProvider.GetRequiredService<ILogger<ChatNeuron>>().LogWarning(error, "Chat source reconciliation failed for {Chat}.", this.GetPrimaryKeyString());
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _renewal?.Dispose();
        if (reason.ReasonCode != DeactivationReasonCode.ShuttingDown && _source is not null)
        {
            try { await _source.Unwatch(this.AsReference<INeuronObserver>()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (SiloUnavailableException) { }
        }
        await base.OnDeactivateAsync(reason, cancellationToken);
    }
}

internal sealed class ChatActivation : IActivationHandler<ChatNeuron>
{
    public Task Activated(ChatNeuron neuron, CancellationToken cancellationToken) => neuron.ActivateBinding();
}
