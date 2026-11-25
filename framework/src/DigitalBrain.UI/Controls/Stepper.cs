namespace DigitalBrain.UI.Controls;

/// <summary>The state of a stepper neuron: only what the user changes. Its steps are the parent's props.</summary>
public sealed record StepperControlState
{
    /// <summary>The active step's id; null makes none active. Stays null while the parent owns the active step.</summary>
    [Showcase("""{"kind":"stepperView","key":"release","activeStepId":"build","props":{"steps":[{"id":"plan","label":"Plan","status":"Done"},{"id":"build","label":"Build","status":"Active","description":"Compiling"},{"id":"test","label":"Test","status":"Error"},{"id":"ship","label":"Ship","status":"Pending"}],"title":"Release","isEnabled":true,"automationName":"Release steps"}}""")]
    [Showcase("""{"kind":"stepperView","key":"locked","activeStepId":null,"props":{"steps":[{"id":"a","label":"Draft","status":"Done"},{"id":"b","label":"Skipped","status":"Skipped"}],"isEnabled":false,"automationName":"Draft steps"}}""")]
    public string? ActiveStepId { get; init; }
}

/// <summary>What a stepper neuron draws: its key and active step. The client draws the title and steps from the slot's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="ActiveStepId">The stepper's own active step; null when none is active or the parent owns the active step.</param>
[ViewNode("stepperView")]
public sealed record StepperView(string Key, string? ActiveStepId) : Node;
