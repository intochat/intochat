namespace DigitalBrain.Framework;

// The kernel as neurons and module code reach it: the script-visible view plus the acts only the
// platform side may take. Not a neuron: nothing here is addressable through IDigitalBrain or MCP.
public interface IKernel : IKernelView
{
    // State belongs to the neuron and is cleared through its own storage (which retires it on its
    // own); this drops the directory entry and announces InstanceRetired.
    // Platform code that runs with no caller (activation hooks) names the brain it works for.
    IAsyncEnumerable<Instance> InstancesIn(string brain, string? type, CancellationToken cancellationToken = default);

    // A contract's single grain type, as the instance directory records it.
    string TypeOf(Type contract);

    Task Retire(string type, string key, CancellationToken cancellationToken = default);
}
