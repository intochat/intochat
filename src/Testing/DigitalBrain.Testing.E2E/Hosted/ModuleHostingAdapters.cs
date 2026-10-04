using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Kernel;
namespace DigitalBrain.Testing.E2E;

internal static class ModuleHostingAdapters
{
    internal static DigitalBrainBuilder AddModules(this DigitalBrainBuilder brain, IReadOnlyList<ModuleDefinition> modules)
    {
        foreach (var module in ModuleComposition.Resolve(modules))
        { brain.WithModule(module.ModuleType, module.Configuration); }
        return brain;
    }
}
