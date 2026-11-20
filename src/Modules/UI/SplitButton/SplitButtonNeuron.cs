using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.SplitButton.Signals;

namespace IntoChat.OS.Modules.UI.SplitButton;

[Neuron("ui.split-button")]
internal sealed class SplitButtonNeuron : Neuron<SplitButtonState>, ISplitButton
{
    public async Task<SplitButtonState> Configure(string primaryLabel, IReadOnlyList<SplitButtonOption> options, string? primaryIcon = null, string? selectedOptionId = null)
    {
        var list = options is not null ? [.. options] : Array.Empty<SplitButtonOption>();
        var next = State with
        {
            Version = State.Version + 1,
            PrimaryLabel = string.IsNullOrWhiteSpace(primaryLabel) ? "Run" : primaryLabel.Trim(),
            PrimaryIcon = primaryIcon,
            Options = list,
            SelectedOptionId = selectedOptionId ?? list.FirstOrDefault()?.Id,
        };
        await Save(next, new SplitButtonInvoked(Id.FullKey(), next.Version, next.SelectedOptionId ?? "primary", next.InvokeCount));
        return next;
    }

    public async Task<SplitButtonState> InvokePrimary()
    {
        var next = State with { Version = State.Version + 1, InvokeCount = State.InvokeCount + 1 };
        await Save(next, new SplitButtonInvoked(Id.FullKey(), next.Version, next.SelectedOptionId ?? "primary", next.InvokeCount));
        return next;
    }

    public async Task<SplitButtonState> SelectOption(string optionId)
    {
        var opt = State.Options.FirstOrDefault(o => string.Equals(o.Id, optionId, StringComparison.Ordinal));
        if (opt is null) { return State; }
        var next = State with
        {
            Version = State.Version + 1,
            PrimaryLabel = opt.Label,
            PrimaryIcon = opt.Icon ?? State.PrimaryIcon,
            SelectedOptionId = opt.Id,
            InvokeCount = State.InvokeCount + 1,
        };
        await Save(next, new SplitButtonInvoked(Id.FullKey(), next.Version, opt.Id, next.InvokeCount));
        return next;
    }

    public Task<SplitButtonState> Read() => Task.FromResult(State);
}
