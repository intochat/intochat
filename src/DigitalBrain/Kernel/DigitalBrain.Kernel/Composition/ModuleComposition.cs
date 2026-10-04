using DigitalBrain.Contracts;
namespace DigitalBrain.Kernel;

public static class ModuleComposition
{
    public static IReadOnlyList<ModuleDefinition> Resolve(IReadOnlyList<ModuleDefinition> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Dictionary<ModuleId, ModuleDefinition> selected = [];
        HashSet<ModuleId> visiting = [];
        List<ModuleDefinition> ordered = [];
        foreach (var module in modules) { Visit(module); }
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

        void Visit(ModuleDefinition module)
        {
            if (visiting.Contains(module.Id)) { throw new InvalidOperationException($"Module dependency cycle at '{module.Id}'."); }
            if (selected.TryGetValue(module.Id, out var previous))
            {
                if (previous.ModuleType != module.ModuleType || previous.Settings.Count != module.Settings.Count ||
                    previous.Settings.Any(pair => !module.Settings.TryGetValue(pair.Key, out var value) || pair.Value != value) ||
                    !previous.Dependencies.Select(d => d.Id).SequenceEqual(module.Dependencies.Select(d => d.Id)))
                { throw new InvalidOperationException($"Conflicting definitions for module '{module.Id}'."); }
                foreach (var dependency in module.Dependencies) { Visit(dependency); }
                return;
            }
            visiting.Add(module.Id);
            foreach (var dependency in module.Dependencies) { Visit(dependency); }
            visiting.Remove(module.Id);
            selected.Add(module.Id, module);
            ordered.Add(module);
        }
    }
}
