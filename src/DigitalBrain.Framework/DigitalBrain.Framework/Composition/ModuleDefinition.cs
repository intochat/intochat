using System.Collections.ObjectModel;
using DigitalBrain;
using DigitalBrain.Framework;
using Orleans.Hosting;

namespace DigitalBrain.Framework;

public sealed class ModuleDefinition
{
    public ModuleDefinition(Module module, IReadOnlyDictionary<string, string?>? settings = null,
        IReadOnlyList<ModuleDefinition>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        Module = module;
        Settings = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase));
        Dependencies = Array.AsReadOnly((dependencies ?? []).ToArray());
    }

    public Module Module { get; }
    public Type ModuleType => Module.GetType();
    public ModuleId Id => Module.Id;
    public IReadOnlyDictionary<string, string?> Settings { get; }
    public IReadOnlyList<ModuleDefinition> Dependencies { get; }
    public void Configure(ISiloBuilder silo) => Module.Configure(silo);
}
