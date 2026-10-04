using System.Reflection;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Kernel;
namespace DigitalBrain.Testing.E2E;

internal static class ModuleHostingAdapters
{
    internal static DigitalBrainBuilder AddModules(this DigitalBrainBuilder brain, IReadOnlyList<ModuleDefinition> modules)
    {
        var adapters = ModuleHostingScan.Resolve(Assembly.GetEntryAssembly()!, typeof(ModuleHostingAdapters).Assembly);
        foreach (var definition in ModuleComposition.Resolve(modules))
        { brain.WithModule(definition.Module, adapters.GetValueOrDefault(definition.Module.Id), definition.Settings); }
        return brain;
    }
}
