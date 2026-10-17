using DigitalBrain;

namespace DigitalBrain.RiskSeal.Signals;

[GenerateSerializer, Alias("riskseal.scored")]
public sealed record RiskScored([property: Id(0)] RiskScore Score) : Signal;
