using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Expander.Signals;

namespace IntoChat.OS.Modules.UI.Expander;

[Neuron("ui.expander")]
internal sealed class ExpanderNeuron : Neuron<ExpanderState>, IExpander
{
    public async Task<ExpanderState> Configure(string title, string body, bool? isExpanded = null, bool isEnabled = true)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "",
            Body = body ?? "",
            IsExpanded = isExpanded ?? State.IsExpanded,
            IsEnabled = isEnabled,
        };
        await Save(next, new ExpanderChanged(Id.FullKey(), next.Version, next.IsExpanded));
        return next;
    }

    public Task<ExpanderState> Toggle() => SetExpanded(!State.IsExpanded);

    public async Task<ExpanderState> SetExpanded(bool isExpanded)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"Expander '{Id.FullKey()}' is disabled.");
        }
        if (State.IsExpanded == isExpanded) { return State; }
        var next = State with { Version = State.Version + 1, IsExpanded = isExpanded };
        await Save(next, new ExpanderChanged(Id.FullKey(), next.Version, next.IsExpanded));
        return next;
    }

    public Task<ExpanderState> Read() => Task.FromResult(State);
}
