using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.BreadcrumbBar;

[NeuronAlias("ui.breadcrumb")]
[DefaultNeuronType("ui.breadcrumb")]
public interface IBreadcrumbBar : IUiInteractiveControl
{
    Task<BreadcrumbBarState> SetItems(IReadOnlyList<BreadcrumbItem> items);
    Task<BreadcrumbBarState> NavigateTo(string itemId);

    [ReadOnlyCall]
    Task<BreadcrumbBarState> Read();
}
