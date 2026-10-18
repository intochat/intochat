using IntoChat.OS;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter;

[GenerateSerializer, Alias("ui.binding-state")]
public sealed class UiBindingState
{
    [Id(0)] public IUiEventHandler? Handler { get; set; }
}

[GenerateSerializer, Alias("ui.binding-changed")]
public sealed record UiBindingChanged([property: Id(0)] string Name) : Signal;

[GrainType("ui.binding")]
internal sealed class UiBindingNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<UiBindingState> store)
    : Neuron<UiBindingState>(store), IUiBinding
{
    public Task Bind(IUiEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Save(new() { Handler = handler }, new UiBindingChanged(this.GetPrimaryKeyString()));
    }
    public Task Dispatch(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Snapshot.Handler?.HandleUiEvent(signal) ?? Task.CompletedTask;
    }
}
