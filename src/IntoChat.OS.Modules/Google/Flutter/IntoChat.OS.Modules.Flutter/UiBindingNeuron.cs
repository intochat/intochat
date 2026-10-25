using IntoChat.OS;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Orleans.Runtime;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter;

[GenerateSerializer, Alias("ui.binding-state")]
[ExportableState]
public sealed class UiBindingState
{
    // Retired (06-07b): bindings stored before the handler was named by id; read once and moved to HandlerId.
    [Id(0)] public IUiEventHandler? Handler { get; set; }

    // The handler's runtime id "brain/type/key".
    [Id(1)] public string? HandlerId { get; set; }
}

[GenerateSerializer, Alias("ui.binding-changed")]
public sealed record UiBindingChanged([property: Id(0)] string Name) : Signal;

[GrainType("ui.binding")]
internal sealed class UiBindingNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<UiBindingState> store)
    : Neuron<UiBindingState>(store), IUiBinding
{
    public Task Bind(string handler)
    {
        var id = UiEventDispatch.Parse(handler);
        var own = NeuronAddress.ScopeOf(this.GetPrimaryKeyString());
        if (id.Brain.Value != own) { throw new UnauthorizedAccessException("A UI binding hands its events to a neuron of its own brain only."); }
        return Save(new() { HandlerId = id.ToString() }, new UiBindingChanged(this.GetPrimaryKeyString()));
    }

    public Task Dispatch(UiEvent uiEvent)
    {
        ArgumentNullException.ThrowIfNull(uiEvent);
        var handler = Snapshot.HandlerId is { } id ? UiEventDispatch.Handler(GrainFactory, UiEventDispatch.Parse(id))
            : Snapshot.Handler;
        // The caller is the one stamped on this dispatch, never what the sender put in the event.
        return handler?.HandleUiEvent(uiEvent with { Caller = UiEventDispatch.Caller() }) ?? Task.CompletedTask;
    }
}

// Transitional (06-07b, R4-17; until the legacy handlers migrate in 06-06a): reaches a bound handler grain by its runtime id. A
// handler contract is implemented by several grain classes (CircleNeuron, CircleFactoryNeuron), so the grain is addressed by its
// own type and key, not resolved from the interface.
internal static class UiEventDispatch
{
    public static DigitalBrain.NeuronId Parse(string handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        try { return DigitalBrain.NeuronId.Parse(handler); }
        catch (FormatException error) { throw new ArgumentException("A handler is named by its runtime id brain/type/key.", nameof(handler), error); }
    }

    public static IUiEventHandler Handler(IGrainFactory grains, DigitalBrain.NeuronId id)
        => grains.GetGrain<IUiEventHandler>(GrainId.Create(id.Type, $"{id.Brain.Value}/{id.Key}"));

    // The caller stamped at the trusted edge this dispatch descends from, or null (a background dispatch).
    public static UiEventCaller? Caller()
        => CallerContextStamper.TryGet(out var caller)
            ? new(caller.PrincipalId, caller.AccountId, caller.BrainId, caller.Kind.ToString(), caller.StampedBy.ToString(), caller.AppId, caller.OnBehalfOf)
            : null;
}
