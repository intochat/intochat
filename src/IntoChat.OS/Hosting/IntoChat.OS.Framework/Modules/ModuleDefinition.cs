using System.Collections.ObjectModel;

namespace IntoChat.OS.Framework.Modules;

public sealed class ModuleDefinition
{
    public ModuleDefinition(Type moduleType, ModuleId id, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        ModuleType = moduleType;
        Id = id;
        Settings = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase));
    }

    public Type ModuleType { get; }
    public ModuleId Id { get; }
    public IReadOnlyDictionary<string, string?> Settings { get; }
}
