using DigitalBrain;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.UI.Core;

/// <summary>
/// WinForms-inspired base class for all stateful UI neurons in <c>IntoChat.OS.Modules.UI</c>.
/// Provides built-in pointer hover/press/focus lifecycle state, standard interaction signals,
/// and theme/typography/iconography/motion attachment without per-control boilerplate.
/// </summary>
public abstract class UiNeuronBase<TState> : Neuron<TState>, IUiInteractiveControl
    where TState : class, new()
{
    private UiInteractionState _interaction = new();

    protected UiInteractionState Interaction => _interaction;
    protected bool IsHovered => _interaction.IsHovered;
    protected bool IsPressed => _interaction.IsPressed;
    protected bool IsFocused => _interaction.IsFocused;

    public virtual async Task<UiInteractionState> OnPointerEnter(UiPointerPayload? pointer = null)
    {
        var payload = pointer ?? new UiPointerPayload();
        if (_interaction.IsHovered) { return _interaction; }
        _interaction = _interaction with { IsHovered = true };
        await PublishEphemeral(new UiPointerEntered(Id.FullKey(), payload));
        return _interaction;
    }

    public virtual async Task<UiInteractionState> OnPointerLeave(UiPointerPayload? pointer = null)
    {
        var payload = pointer ?? new UiPointerPayload();
        if (!_interaction.IsHovered && !_interaction.IsPressed) { return _interaction; }
        _interaction = _interaction with { IsHovered = false, IsPressed = false };
        await PublishEphemeral(new UiPointerLeft(Id.FullKey(), payload));
        return _interaction;
    }

    public virtual async Task<UiInteractionState> OnPointerDown(UiPointerPayload? pointer = null)
    {
        var payload = pointer ?? new UiPointerPayload();
        _interaction = _interaction with { IsPressed = true };
        await PublishEphemeral(new UiPointerPressed(Id.FullKey(), payload));
        return _interaction;
    }

    public virtual async Task<UiInteractionState> OnPointerUp(UiPointerPayload? pointer = null)
    {
        var payload = pointer ?? new UiPointerPayload();
        _interaction = _interaction with { IsPressed = false };
        await PublishEphemeral(new UiPointerReleased(Id.FullKey(), payload));
        return _interaction;
    }

    public virtual async Task<UiInteractionState> OnFocusChanged(bool focused)
    {
        if (_interaction.IsFocused == focused) { return _interaction; }
        _interaction = _interaction with { IsFocused = focused };
        Signal signal = focused
            ? new UiFocusGained(Id.FullKey())
            : new UiFocusLost(Id.FullKey());
        await PublishEphemeral(signal);
        return _interaction;
    }

    public virtual Task<UiInteractionState> ConfigureInteraction(
        UiEventMask subscribedEvents = UiEventMask.Click,
        UiStyleRef? style = null,
        UiMotionPreset motion = UiMotionPreset.Standard)
    {
        _interaction = _interaction with
        {
            SubscribedEvents = subscribedEvents,
            Style = style ?? _interaction.Style,
            Motion = motion,
        };
        return Task.FromResult(_interaction);
    }

    public Task<UiInteractionState> ReadInteraction() => Task.FromResult(_interaction);
}
