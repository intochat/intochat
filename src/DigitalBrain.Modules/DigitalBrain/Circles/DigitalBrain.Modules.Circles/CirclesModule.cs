using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute.
public sealed class CirclesModule() : Module(new ModuleId("circles"))
{
    public override System.Reflection.Assembly? Contracts => typeof(ICircle).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddIncomingGrainCallFilter<CircleCallGuard>();
        silo.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICreationHandler, CircleBirth>());
    }
}
