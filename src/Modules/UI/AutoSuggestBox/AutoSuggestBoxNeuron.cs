using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.AutoSuggestBox.Signals;

namespace IntoChat.OS.Modules.UI.AutoSuggestBox;

[Neuron("ui.auto-suggest")]
internal sealed class AutoSuggestBoxNeuron : UiNeuronBase<AutoSuggestBoxState>, IAutoSuggestBox
{
    public async Task<AutoSuggestBoxState> Configure(string placeholder, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToArray();
        var next = State with
        {
            Version = State.Version + 1,
            Placeholder = string.IsNullOrWhiteSpace(placeholder) ? "Search…" : placeholder,
            Items = list,
            Suggestions = FilterSuggestions(list, State.Query),
        };
        await Save(next, new AutoSuggestChanged(Id.FullKey(), next.Version, next.Query, next.SubmittedText));
        return next;
    }

    public async Task<AutoSuggestBoxState> SetQuery(string query)
    {
        var q = query ?? "";
        var next = State with
        {
            Version = State.Version + 1,
            Query = q,
            Suggestions = FilterSuggestions(State.Items, q),
        };
        await Save(next, new AutoSuggestChanged(Id.FullKey(), next.Version, next.Query, next.SubmittedText));
        return next;
    }

    public async Task<AutoSuggestBoxState> Submit(string text)
    {
        var trimmed = (text ?? "").Trim();
        var next = State with
        {
            Version = State.Version + 1,
            Query = trimmed,
            SubmittedText = trimmed,
            Suggestions = FilterSuggestions(State.Items, trimmed),
        };
        await Save(next, new AutoSuggestChanged(Id.FullKey(), next.Version, next.Query, next.SubmittedText));
        return next;
    }

    public Task<AutoSuggestBoxState> Read() => Task.FromResult(State);

    private static string[] FilterSuggestions(IReadOnlyList<string> items, string query)
        => string.IsNullOrWhiteSpace(query)
            ? [.. items.Take(8)]
            : [.. items.Where(i => i.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(8)];
}
