using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using ModuleContract = DigitalBrain.Framework.Modules.Module;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Testing.Module;

internal sealed class ModuleDraft
{
    public ModuleContract Module { get; }
    public ModuleOptionsSlot? Options { get; private init; }
    public List<Action<IServiceCollection>> LocalServices { get; } = [];

    public ModuleDraft(ModuleContract module)
    {
        Module = module;
        Options = ModuleOptionsSlot.For(module.GetType());
    }

    public ModuleDefinition Compile() => Options?.Compile() ?? new ModuleDefinition(Module);
    public ModuleDraft Copy()
    {
        var copy = new ModuleDraft(Module) { Options = Options?.Clone() };
        copy.LocalServices.AddRange(LocalServices);
        return copy;
    }
}
