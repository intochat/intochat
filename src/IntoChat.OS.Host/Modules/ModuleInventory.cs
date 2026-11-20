using System.Reflection;
using DigitalBrain.Brains;

namespace IntoChat.OS.Host.Modules;

// The modules selected for this host, available to modules without coupling hosting to their consumers.
public sealed class ModuleInventory
{
    public ModuleInventory(IEnumerable<OsModule> modules, IReadOnlyList<ModuleStatus>? statuses = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = Array.AsReadOnly(modules.ToArray());
        Statuses = statuses ?? Array.AsReadOnly(Modules.Select(module => new ModuleStatus(module.Id, ModuleState.Active, null)).ToArray());
        Types = Array.AsReadOnly(Modules.Select(module => module.Type).ToArray());
    }

    public IReadOnlyList<OsModule> Modules { get; }
    public IReadOnlyList<Type> Types { get; }

    // Every module the server registered and what became of it: running, unlicensed, or disabled by configuration.
    public IReadOnlyList<ModuleStatus> Statuses { get; }

    // Every contract assembly this composition exposes: the words, the kernel's connection contracts,
    // and each module's declared contracts. The one answer to "which neuron contracts exist here".
    public IReadOnlyList<Assembly> ContractAssemblies()
        => [.. new[] { typeof(IBrain).Assembly, ConnectionContracts() }
            .OfType<Assembly>()
            .Concat(Modules.Select(module => module.Contracts).OfType<Assembly>())
            .Distinct()];

    // Connection contracts live alongside ModuleInventory in IntoChat.OS.Host.
    private static Assembly ConnectionContracts() => typeof(ModuleInventory).Assembly;

    public OsModule? Declaring(Assembly contracts) => Modules.FirstOrDefault(module => module.Contracts == contracts);
}
