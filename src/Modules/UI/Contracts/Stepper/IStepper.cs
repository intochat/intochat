using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Stepper;

public static class StepperStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Done = "done";
    public const string Error = "error";
    public const string Skipped = "skipped";

    public static string Normalize(string? status) => (status ?? Pending).Trim().ToLowerInvariant() switch
    {
        "active" or "running" or "working" or "in-progress" => Active,
        "done" or "completed" or "complete" or "succeeded" => Done,
        "error" or "failed" or "refused" => Error,
        "skipped" => Skipped,
        _ => Pending,
    };
}

[GenerateSerializer, Alias("ui.stepper.step")]
public sealed record StepperStep(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Status = StepperStatuses.Pending,
    [property: Id(3)] string? Subtitle = null,
    [property: Id(4)] string? Icon = null)
{
    public StepperStep() : this("", "", StepperStatuses.Pending, null, null) { }
}

[GenerateSerializer, Alias("ui.stepper.state")]
public sealed record StepperState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] StepperStep[] Steps,
    [property: Id(3)] string? ActiveStepId,
    [property: Id(4)] bool AllowInteractiveSelection)
{
    public StepperState() : this(0, "", [], null, true) { }
}

[NeuronAlias("ui.stepper")]
[DefaultNeuronType("ui.stepper")]
public interface IStepper
{
    Task<StepperState> Configure(IReadOnlyList<StepperStep> steps, string? activeStepId = null, string title = "", bool allowInteractiveSelection = true);
    Task<StepperState> Select(string stepId);
    Task<StepperState> SetStepStatus(string stepId, string status, string? subtitle = null);
    Task<StepperState> Advance();
    [ReadOnlyCall] Task<StepperState> Read();
}
