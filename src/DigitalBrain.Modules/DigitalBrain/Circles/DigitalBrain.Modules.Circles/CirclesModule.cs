using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Circles;

// Circles: brains that share a conversation, apps and a share of the owner's compute, inside one cluster.
public sealed class CirclesModule() : Module(new ModuleId("circles"))
{
    public override System.Reflection.Assembly? Contracts => typeof(ICircle).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
