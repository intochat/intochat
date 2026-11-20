using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Stepper.Signals;

[Signal("ui.stepper-changed")]
public sealed record StepperChanged(string StepperId, int Version, string? ActiveStepId) : Signal;
