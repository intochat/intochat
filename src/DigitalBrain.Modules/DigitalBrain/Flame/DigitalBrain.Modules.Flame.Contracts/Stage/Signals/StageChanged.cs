using DigitalBrain;

namespace DigitalBrain.Flame.Stage.Signals;

[GenerateSerializer, Alias("flame.stage-changed")]
public sealed record StageChanged([property: Id(0)] string Name, [property: Id(1)] long Revision, [property: Id(2)] string Top) : Signal;
