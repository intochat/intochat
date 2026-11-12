using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Os.Contracts;

namespace IntoChat.OS.Modules.Os;

[Neuron("os.system")]
public sealed class OsNeuron : DigitalBrain.Neurons.Neuron, IOsNeuron
{
    public Task<string> Status() => Task.FromResult("healthy");
}
