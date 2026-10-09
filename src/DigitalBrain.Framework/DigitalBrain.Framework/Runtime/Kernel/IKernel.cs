namespace DigitalBrain.Framework;

// The kernel as neurons and module code reach it: the script-visible view plus the acts only the
// platform side may take. Not a neuron: nothing here is addressable through IDigitalBrain or MCP.
public interface IKernel : IKernelView
{
    // State belongs to the neuron and is cleared through its own storage (which retires it on its
    // own); this drops the directory entry and announces InstanceRetired.
    Task Retire(string type, string key, CancellationToken cancellationToken = default);
}
