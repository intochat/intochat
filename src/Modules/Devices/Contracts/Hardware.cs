using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Devices;

[JsonConverter(typeof(JsonStringEnumConverter<Accelerator>))]
public enum Accelerator
{
    Cpu,
    Cuda,
    DirectML,
    Npu,
    Metal,
}

[JsonConverter(typeof(JsonStringEnumConverter<Fit>))]
public enum Fit
{
    Good,
    Slow,
    WontFit,
}

[GenerateSerializer, Alias("devices.hardware")]
public sealed record Hardware(
    [property: Id(0)] long RamBytes,
    [property: Id(1)] long VramBytes,
    [property: Id(2)] Accelerator[] Accelerators);
