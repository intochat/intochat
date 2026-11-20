using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.InfoBadge;

[GenerateSerializer, Alias("ui.info-badge.state")]
public sealed record InfoBadgeState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Label,
    [property: Id(2)] int? Value,
    [property: Id(3)] string? Icon,
    [property: Id(4)] string Severity,
    [property: Id(5)] string Kind,
    [property: Id(6)] bool IsVisible)
{
    public InfoBadgeState() : this(0, "", null, null, "attention", "dot", true) { }
}

[NeuronAlias("ui.info-badge")]
[DefaultNeuronType("ui.info-badge")]
public interface IInfoBadge : IUiInteractiveControl
{
    Task<InfoBadgeState> Configure(string label, int? value = null, string severity = "attention", string? icon = null, bool isVisible = true);
    Task<InfoBadgeState> SetValue(int? value);
    Task<InfoBadgeState> SetSeverity(string severity);
    Task<InfoBadgeState> SetVisible(bool isVisible);
    [ReadOnlyCall] Task<InfoBadgeState> Read();
}
