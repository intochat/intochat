using DigitalBrain;

namespace IntoChat.OS.Modules.UI.BreadcrumbBar.Signals;

[Signal("ui.breadcrumb-clicked")]
public sealed record BreadcrumbClicked(string BreadcrumbId, int Version, string ItemId) : Signal;
