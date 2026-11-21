using DigitalBrain.State;

namespace IntoChat.OS.Modules.Devices;

[GenerateSerializer, Alias("devices.device-state"), ExportableState]
internal sealed record DeviceState
{
    [Id(0)] public Install[] Queue { get; init; } = [];
    [Id(1)] public Install[] Recent { get; init; } = [];
}
