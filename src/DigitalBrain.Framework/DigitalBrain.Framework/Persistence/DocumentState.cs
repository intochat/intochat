using Orleans;

namespace DigitalBrain.Framework;

[GenerateSerializer, Alias("DigitalBrain.Kernel.DocumentState")]
public sealed class DocumentState
{
    [Id(0)] public bool Written { get; set; }
    [Id(1)] public long Version { get; set; }
    [Id(2)] public string Payload { get; set; } = "";
}
