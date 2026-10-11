using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.RiskSeal;

[GenerateSerializer, Alias("riskseal.state")]
internal sealed class RiskSealState : ConnectionState
{
    public const int MaxScores = 50;

    // Oldest first.
    [Id(8)] public RiskScore[] Scores { get; set; } = [];
}
