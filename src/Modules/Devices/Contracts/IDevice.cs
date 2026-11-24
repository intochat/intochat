using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Devices;

[GenerateSerializer, Alias("devices.device-model")]
public sealed record DeviceModel(
    [property: Id(0)] ModelCard Card,
    [property: Id(1)] RuntimeId Runtime,
    [property: Id(2)] InstallState State,
    [property: Id(3)] Fit Fit);

[GenerateSerializer, Alias("devices.device-view")]
public sealed record DeviceView(
    [property: Id(0)] string MachineId,
    [property: Id(1)] Hardware Hardware,
    [property: Id(2)] RuntimeStatus[] Runtimes,
    [property: Id(3)] DeviceModel[] Models,
    [property: Id(4)] Install[] Queue);

[NeuronAlias("devices.device"), DefaultNeuronType("devices.device")]
public interface IDevice
{
    static string Key(string brainId, string machineId) => $"{brainId}/devices/{machineId}";

    [ReadOnlyCall, Interleaves]
    Task<DeviceView> Read();

    Task Use(ModelRole role, ModelId model);

    Task Install(ModelId model);

    Task Cancel(ModelId model);

    Task Remove(ModelId model);
}

// Same neuron; the installer's only way back in; not an agent tool.
[NeuronAlias("devices.install-reports"), DefaultNeuronType("devices.device")]
public interface IDeviceInstallReports
{
    Task Report(InstallProgress progress);
}
