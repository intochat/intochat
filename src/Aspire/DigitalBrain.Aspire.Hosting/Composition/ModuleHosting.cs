using DigitalBrain;
using DigitalBrain.Kernel;

namespace DigitalBrain.Aspire.Hosting;

// The generic argument is the module↔ring link: a compiler-checked project reference the hosting
// ring already has, so adapter and module cannot disagree on the id.
public abstract class ModuleHosting<TModule> : IDigitalBrainModuleHosting where TModule : Module, new()
{
    public ModuleId Module { get; } = new TModule().Id;
    public abstract void Configure(DigitalBrainBuilder brain);
}
