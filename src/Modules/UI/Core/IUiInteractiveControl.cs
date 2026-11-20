using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Core;

/// <summary>
/// Common WinForms-inspired interaction, styling, and motion contract implemented by
/// every stateful UI neuron in <c>IntoChat.OS.Modules.UI</c>.
/// </summary>
public interface IUiInteractiveControl
{
    Task<UiInteractionState> OnPointerEnter(UiPointerPayload? pointer = null);
    Task<UiInteractionState> OnPointerLeave(UiPointerPayload? pointer = null);
    Task<UiInteractionState> OnPointerDown(UiPointerPayload? pointer = null);
    Task<UiInteractionState> OnPointerUp(UiPointerPayload? pointer = null);
    Task<UiInteractionState> OnFocusChanged(bool focused);

    Task<UiInteractionState> ConfigureInteraction(
        UiEventMask subscribedEvents = UiEventMask.Click,
        UiStyleRef? style = null,
        UiMotionPreset motion = UiMotionPreset.Standard);

    [ReadOnlyCall]
    Task<UiInteractionState> ReadInteraction();
}
