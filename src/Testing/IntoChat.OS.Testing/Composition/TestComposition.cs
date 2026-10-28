using System.Text.Json;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Testing;

public sealed class BrainComposition
{
    public BrainComposition(IReadOnlyDictionary<string, string?> optionOverrides, IReadOnlyList<Type> digitalModules)
    {
        OptionOverrides = optionOverrides;
        DigitalModules = digitalModules;
    }

    public IReadOnlyList<ModuleDefinition> Modules { get; } = [];
    public IReadOnlyList<Type> DigitalModules { get; }
    public IReadOnlyDictionary<string, string?> OptionOverrides { get; }
}

public sealed class BrainCompositionBuilder
{
    private readonly List<Type> _digital = [];
    private readonly Dictionary<string, IDigitalOptionBag> _digitalOptions = new(StringComparer.OrdinalIgnoreCase);

    public BrainCompositionBuilder RequireModules(IEnumerable<OsModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules)
        {
            var type = module.Neurons ?? module.Type;
            if (!_digital.Contains(module.Type) && !_digital.Contains(type)) { _digital.Add(type); }
        }
        return this;
    }

    public void RememberDigital(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!_digital.Contains(type)) { _digital.Add(type); }
    }

    public void ProjectDigitalOptions<TOptions>(string moduleId, Action<TOptions> configure)
        where TOptions : class, IModuleOptions, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(configure);
        if (!_digitalOptions.TryGetValue(moduleId, out var bag)) { _digitalOptions[moduleId] = bag = new DigitalOptionBag<TOptions>(); }
        bag.Edit(configure);
    }

    public BrainComposition Build()
    {
        Dictionary<string, string?> overrides = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, bag) in _digitalOptions)
        {
            overrides[IntoChatOSConfiguration.ModuleSection(id) + ":ReplaceOptions"] = "true";
            foreach (var (key, value) in bag.Flatten(id)) { if (value is not null) { overrides[key] = value; } }
        }
        return new(overrides, _digital.AsReadOnly());
    }
}

internal interface IDigitalOptionBag
{
    void Edit(Delegate configure);
    IReadOnlyDictionary<string, string?> Flatten(string moduleId);
}

internal sealed class DigitalOptionBag<TOptions> : IDigitalOptionBag where TOptions : class, IModuleOptions, new()
{
    private TOptions _options = new();

    public void Edit(Delegate configure)
    {
        var working = RoundTrip(_options);
        ((Action<TOptions>)configure)(working);
        _options = RoundTrip(working);
    }

    public IReadOnlyDictionary<string, string?> Flatten(string moduleId)
    {
        ModuleOptionsSerialization.Validate(_options, moduleId);
        return ModuleOptionsSerialization.FlattenOptions(_options, moduleId);
    }

    private static TOptions RoundTrip(TOptions options)
        => JsonSerializer.Deserialize<TOptions>(JsonSerializer.Serialize(options))!;
}
