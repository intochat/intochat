using System.Text.Json.Serialization;
using DigitalBrain;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Devices;

[JsonConverter(typeof(JsonStringEnumConverter<InstallState>))]
public enum InstallState
{
    Available,
    Queued,
    Downloading,
    Verifying,
    Ready,
    Loaded,
    Failed,
    Cancelled,
}

[GenerateSerializer, Alias("devices.install-progress")]
public sealed record InstallProgress(
    [property: Id(0)] ModelId Model,
    [property: Id(1)] InstallState State,
    [property: Id(2)] double Percent,
    [property: Id(3)] long Bytes,
    [property: Id(4)] long TotalBytes,
    [property: Id(5)] string? Error = null);

[GenerateSerializer, Alias("devices.install")]
public sealed record Install(
    [property: Id(0)] ModelId Model,
    [property: Id(1)] RuntimeId Runtime,
    [property: Id(2)] InstallState State,
    [property: Id(3)] double Percent,
    [property: Id(4)] long Bytes,
    [property: Id(5)] long TotalBytes,
    [property: Id(6)] string? Error,
    [property: Id(7)] Role? SwitchWhenReady);

[Signal("devices.install-changed")]
public sealed record InstallChanged(ModelId Model, InstallState State, double Percent) : Signal;
