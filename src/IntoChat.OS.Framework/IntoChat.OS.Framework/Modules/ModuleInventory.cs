using System.Reflection;

namespace IntoChat.OS.Framework.Modules;

// The modules selected for this host, available to modules without coupling hosting to their consumers.
public sealed class ModuleInventory
{
    public ModuleInventory(IEnumerable<Module> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = Array.AsReadOnly(modules.ToArray());
        Types = Array.AsReadOnly(Modules.Select(module => module.GetType()).ToArray());
    }

    public IReadOnlyList<Module> Modules { get; }
    public IReadOnlyList<Type> Types { get; }

    // Every contract assembly this composition exposes: the words, the kernel's connection contracts,
    // and each module's declared contracts. The one answer to "which neuron contracts exist here".
    public IReadOnlyList<Assembly> ContractAssemblies()
        => [.. new[] { typeof(INeuron).Assembly, typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly }
            .Concat(Modules.Select(module => module.Contracts).OfType<Assembly>())
            .Distinct()];

    public Module? Declaring(Assembly contracts) => Modules.FirstOrDefault(module => module.Contracts == contracts);
}
