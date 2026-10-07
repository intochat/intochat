using DigitalBrain.Framework;

namespace DigitalBrain.Testing.Module;

public static class ModuleComposition
{
    public static IReadOnlyList<ModuleDefinition> Resolve(IReadOnlyList<ModuleDefinition> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Dictionary<ModuleId, ModuleDefinition> selected = [];
        List<ModuleDefinition> ordered = [];
        foreach (var module in modules)
        {
            if (selected.TryGetValue(module.Id, out var previous))
            {
                if (previous.ModuleType != module.ModuleType || previous.Settings.Count != module.Settings.Count ||
                    previous.Settings.Any(pair => !module.Settings.TryGetValue(pair.Key, out var value) || pair.Value != value))
                { throw new InvalidOperationException($"Conflicting definitions for module '{module.Id}'."); }
                continue;
            }
            selected.Add(module.Id, module);
            ordered.Add(module);
        }
        Dictionary<string, string?> configuration = new(StringComparer.OrdinalIgnoreCase);
        foreach (var module in ordered)
        {
            foreach (var pair in module.Settings)
            {
                if (configuration.TryGetValue(pair.Key, out var value) && value != pair.Value)
                { throw new InvalidOperationException($"Conflicting configuration key '{pair.Key}'."); }
                configuration[pair.Key] = pair.Value;
            }
        }
        return ordered.AsReadOnly();
    }
}
