using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Devices;

[Neuron("devices.device")]
internal sealed class DeviceNeuron : Neuron<DeviceState>, IDevice, IDeviceInstallReports
{
    public Task<DeviceView> Read()
        => Task.FromResult(new DeviceView(string.Empty, new Hardware(0, 0, [Accelerator.Cpu]), [], [], [.. State.Queue, .. State.Recent]));

    public Task Use(Role role, ModelId model) => Task.CompletedTask;

    public Task Install(ModelId model) => Task.CompletedTask;

    public Task Cancel(ModelId model) => Task.CompletedTask;

    public Task Remove(ModelId model) => Task.CompletedTask;

    public Task Report(InstallProgress progress) => Task.CompletedTask;
}
