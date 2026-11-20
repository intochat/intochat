using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.InfoBadge.Signals;

namespace IntoChat.OS.Modules.UI.InfoBadge;

[Neuron("ui.info-badge")]
internal sealed class InfoBadgeNeuron : Neuron<InfoBadgeState>, IInfoBadge
{
    public async Task<InfoBadgeState> Configure(string label, int? value = null, string severity = "attention", string? icon = null, bool isVisible = true)
    {
        var normVal = value is < 0 ? 0 : value;
        var kind = normVal is not null ? "count" : !string.IsNullOrWhiteSpace(icon) ? "icon" : "dot";
        var next = State with
        {
            Version = State.Version + 1,
            Label = label?.Trim() ?? "",
            Value = normVal,
            Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim(),
            Severity = NormalizeSeverity(severity),
            Kind = kind,
            IsVisible = isVisible,
        };
        await Save(next, new InfoBadgeChanged(Id.FullKey(), next.Version, next.Value, next.Severity, next.IsVisible));
        return next;
    }

    public async Task<InfoBadgeState> SetValue(int? value)
    {
        var normVal = value is < 0 ? 0 : value;
        var kind = normVal is not null ? "count" : State.Icon is not null ? "icon" : "dot";
        var next = State with
        {
            Version = State.Version + 1,
            Value = normVal,
            Kind = kind,
        };
        await Save(next, new InfoBadgeChanged(Id.FullKey(), next.Version, next.Value, next.Severity, next.IsVisible));
        return next;
    }

    public async Task<InfoBadgeState> SetSeverity(string severity)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Severity = NormalizeSeverity(severity),
        };
        await Save(next, new InfoBadgeChanged(Id.FullKey(), next.Version, next.Value, next.Severity, next.IsVisible));
        return next;
    }

    public async Task<InfoBadgeState> SetVisible(bool isVisible)
    {
        var next = State with
        {
            Version = State.Version + 1,
            IsVisible = isVisible,
        };
        await Save(next, new InfoBadgeChanged(Id.FullKey(), next.Version, next.Value, next.Severity, next.IsVisible));
        return next;
    }

    public Task<InfoBadgeState> Read() => Task.FromResult(State);

    private static string NormalizeSeverity(string? severity) => (severity ?? "attention").Trim().ToLowerInvariant() switch
    {
        "success" or "done" => "success",
        "caution" or "warning" => "caution",
        "critical" or "error" or "danger" => "critical",
        "informational" or "info" or "neutral" => "informational",
        _ => "attention",
    };
}
