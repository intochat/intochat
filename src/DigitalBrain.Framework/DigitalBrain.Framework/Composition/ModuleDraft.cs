using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework;

internal sealed class ModuleDraft
{
    public Module Module { get; }
    public ModuleOptionsSlot? Options { get; private init; }
    public List<Action<IServiceCollection>> LocalServices { get; } = [];

    public ModuleDraft(Module module)
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
