namespace IntoChat.OS.Framework;

// The kernel as neurons and module code reach it: the script-visible view plus the acts only the
// platform side may take. Not a neuron: nothing here is addressable through IIntoChatOS or MCP.
public interface IKernel : IKernelView
{
    // Platform code that runs with no caller (activation hooks) names the brain it works for.
    IAsyncEnumerable<Instance> InstancesIn(string brain, string? type, CancellationToken cancellationToken = default);

    // A contract's single grain type, as the instance directory records it.
    string TypeOf(Type contract);

    // State belongs to the neuron and is cleared through its own storage (which retires it on its
    // own); this drops the directory entry and announces InstanceRetired.
    Task Retire(string type, string key, CancellationToken cancellationToken = default);

    // The features loaded into a brain. The app neuron records its own program here whenever it is
    // assigned, so features from disk, the Marketplace and the Creator land the same way.
    Task<Feature[]> Features(string brain, CancellationToken cancellationToken = default);

    Task LoadFeature(string brain, Feature feature, TypeEntry[] types, CancellationToken cancellationToken = default);

    Task UnloadFeature(string brain, string name, CancellationToken cancellationToken = default);
}
