using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Flutter;

[Alias("ui.event-handler")]
public interface IUiEventHandler : IGrainWithStringKey
{
    [OneWay] Task HandleUiEvent(Signal signal);
}

[Alias("ui.binding"), Orleans.Metadata.DefaultGrainType("ui.binding")]
public interface IUiBinding : INeuron
{
    Task Bind(IUiEventHandler handler);
    Task Dispatch(Signal signal);
}
