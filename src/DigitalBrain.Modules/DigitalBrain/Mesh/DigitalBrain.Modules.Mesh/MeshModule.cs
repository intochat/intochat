using System.Reflection;
using DigitalBrain.Framework.Modules;
using Orleans.Hosting;

namespace DigitalBrain.Mesh;

// Storage for brain data on machines nobody has to trust: callers encrypt, nodes hold shards of ciphertext.
public sealed class MeshModule() : Module<MeshOptions>(new ModuleId("mesh"))
{
    public override Assembly? Contracts => typeof(IStorageNode).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
