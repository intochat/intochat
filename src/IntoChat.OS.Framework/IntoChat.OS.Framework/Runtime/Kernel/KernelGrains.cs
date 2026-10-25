// The kernel grains' contracts. The grains themselves live in the kernel composition (06-02).
namespace IntoChat.OS.Framework;

// One partition of the instance directory: every instance of one grain type in one brain, keyed
// "{brain}/{type}". Not an INeuron, so no brain.Get, contract catalog or MCP surface can reach it.
[Alias("kernel.instance-directory")]
internal interface IInstanceDirectory : IGrainWithStringKey
{
    Task Register(string key, string? creator);
    Task Retire(string key);
    Task<Instance[]> List();
}

// The grain types a brain has instances of, keyed "{brain}".
[Alias("kernel.instance-types")]
internal interface IInstanceTypes : IGrainWithStringKey
{
    Task Add(string type);
    Task<string[]> List();
}

// The brain neuron publishes the kernel's instance signals, so they arrive with the brain as publisher.
[Alias("kernel.announcer")]
internal interface IKernelAnnouncer : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Announce(Signal signal);
}

internal static class InstanceDirectoryKey
{
    public static string Of(string brain, string type) => brain + "/" + type;
}

internal static class TypeTableKey
{
    // A slash is never a brain id, so no brain's features can land under it.
    public const string NoBrain = "/";
}

// The types a brain knows, keyed "{brain}": the host's compiled layer, then the brain's own features.
// Unscoped discovery has no brain journal and must never activate persistent feature state.
[Alias("kernel.compiled-type-table")]
internal interface ICompiledTypeTable : IGrainWithStringKey
{
    Task<TypeEntry[]> List();
}

[Alias("kernel.type-table")]
internal interface ITypeTable : IGrainWithStringKey
{
    Task<TypeEntry[]> List();
    Task<Feature[]> Features();
    Task Load(Feature feature, TypeEntry[] types);
    Task Unload(string name);
}

