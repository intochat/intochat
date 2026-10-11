using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute. A member's own brain creates and lists its circles.
public sealed class CirclesModule() : Module(new ModuleId("circles"))
{
    public override System.Reflection.Assembly? Contracts => typeof(ICircle).Assembly;

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        // One birth, reached as a creation handler and by the factory (which completes a birth that failed).
        if (silo.Services.Any(descriptor => descriptor.ServiceType == typeof(CircleBirth))) { return; }
        silo.Services.AddSingleton<CircleBirth>();
        silo.Services.AddSingleton<ICreationHandler>(provider => provider.GetRequiredService<CircleBirth>());
    }
}
