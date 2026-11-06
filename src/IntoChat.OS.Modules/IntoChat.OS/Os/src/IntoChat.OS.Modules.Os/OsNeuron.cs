using DigitalBrain;
using IntoChat.OS.Modules.Os.Contracts;

namespace IntoChat.OS.Modules.Os;

[Neuron("os.system")]
public sealed class OsNeuron : DigitalBrain.Neuron, IOsNeuron
{
    public Task<string> Status() => Task.FromResult("healthy");
}
