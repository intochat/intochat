using System.Collections.ObjectModel;

namespace IntoChat.OS.Framework.Modules;

public sealed class ModuleDefinition
{
    public ModuleDefinition(Module module, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        Module = module;
        Settings = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase));
    }

    public Module Module { get; }
    public Type ModuleType => Module.GetType();
    public ModuleId Id => Module.Id;
    public IReadOnlyDictionary<string, string?> Settings { get; }
    public void Configure(ISiloBuilder silo) => Module.Configure(silo);
}
