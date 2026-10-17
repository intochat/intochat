using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Equifax;

[GenerateSerializer, Alias("equifax.state")]
internal sealed class EquifaxState : ConnectionState
{
    // Newest first, at most EquifaxNeuron.MaxReports.
    [Id(8)] public BureauReport[] Reports { get; set; } = [];
}
