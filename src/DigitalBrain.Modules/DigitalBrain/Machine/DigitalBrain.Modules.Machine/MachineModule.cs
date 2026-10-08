using System.Reflection;
using DigitalBrain.Framework.Modules;
using Orleans.Hosting;

namespace DigitalBrain.Machine;

// The person's PC as an edge: a shell command surface and a file surface. The OS composes it; the
// router (Jev) picks it and the assistant drives it to act on the machine.
public sealed class MachineModule() : Module<MachineOptions>(new ModuleId("machine"))
{
    public override Assembly? Contracts => typeof(IConsole).Assembly;

    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
