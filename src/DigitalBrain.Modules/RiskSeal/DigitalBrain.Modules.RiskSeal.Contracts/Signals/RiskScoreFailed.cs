using DigitalBrain;

namespace DigitalBrain.RiskSeal.Signals;

// Detail is the provider's status ("unauthorized (401)", "503", "timeout"), never a credential.
[GenerateSerializer, Alias("riskseal.score-failed")]
public sealed record RiskScoreFailed([property: Id(0)] string RequestId, [property: Id(1)] string Detail) : Signal;
