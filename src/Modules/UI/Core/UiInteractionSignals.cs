using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Core;

/// <summary>
/// Opt-in bitmask declaring which WinForms-style pointer and focus events the Flutter
/// <c>UiControlSurface</c> forwards back to the C# neuron over gRPC/Orleans.
/// Local Flutter hover/press/focus animations ALWAYS run at 120fps regardless of mask.
/// </summary>
[Flags]
public enum UiEventMask
{
    None = 0,
    Click = 1 << 0,
    Hover = 1 << 1,
    Press = 1 << 2,
    Focus = 1 << 3,
    ContextMenu = 1 << 4,
    Keyboard = 1 << 5,
    All = Click | Hover | Press | Focus | ContextMenu | Keyboard,
}

/// <summary>Normalized pointer coordinates and button metadata from Flutter.</summary>
[GenerateSerializer, Alias("ui.core.pointer-payload")]
public sealed record UiPointerPayload(
    [property: Id(0)] double LocalX = 0,
    [property: Id(1)] double LocalY = 0,
    [property: Id(2)] int Buttons = 0,
    [property: Id(3)] string DeviceKind = "mouse");

/// <summary>Live WinForms-style interaction state tracked uniformly by <see cref="UiNeuronBase{TState}"/>.</summary>
[GenerateSerializer, Alias("ui.core.interaction-state")]
public sealed record UiInteractionState(
    [property: Id(0)] bool IsHovered = false,
    [property: Id(1)] bool IsPressed = false,
    [property: Id(2)] bool IsFocused = false,
    [property: Id(3)] UiEventMask SubscribedEvents = UiEventMask.Click,
    [property: Id(4)] UiStyleRef? Style = null,
    [property: Id(5)] UiMotionPreset Motion = UiMotionPreset.Standard);

[Signal("ui.pointer-entered")]
public sealed record UiPointerEntered(
    string ControlId,
    UiPointerPayload Pointer) : Signal;

[Signal("ui.pointer-left")]
public sealed record UiPointerLeft(
    string ControlId,
    UiPointerPayload Pointer) : Signal;

[Signal("ui.pointer-pressed")]
public sealed record UiPointerPressed(
    string ControlId,
    UiPointerPayload Pointer) : Signal;

[Signal("ui.pointer-released")]
public sealed record UiPointerReleased(
    string ControlId,
    UiPointerPayload Pointer) : Signal;

[Signal("ui.focus-gained")]
public sealed record UiFocusGained(string ControlId) : Signal;

[Signal("ui.focus-lost")]
public sealed record UiFocusLost(string ControlId) : Signal;
