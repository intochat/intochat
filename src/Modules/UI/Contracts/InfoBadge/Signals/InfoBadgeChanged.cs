using DigitalBrain;

namespace IntoChat.OS.Modules.UI.InfoBadge.Signals;

[Signal("ui.info-badge-changed")]
public sealed record InfoBadgeChanged(string BadgeId, int Version, int? Value, string Severity, bool IsVisible) : Signal;
