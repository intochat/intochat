using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using ModuleContract = DigitalBrain.Framework.Modules.Module;

namespace DigitalBrain.Testing;

public sealed class BrainComposition
{
    private readonly Action<IServiceCollection>[] _localServices;
    internal BrainComposition(IReadOnlyList<ModuleDefinition> modules, Action<IServiceCollection>[] localServices)
    {
        Modules = modules;
        _localServices = localServices;
    }
    public IReadOnlyList<ModuleDefinition> Modules { get; }
    public bool RequiresLocalServices => _localServices.Length > 0;
    public void ConfigureLocalServices(IServiceCollection services)
    {
        foreach (var configure in _localServices) { configure(services); }
    }
}

public sealed class BrainCompositionBuilder
{
    private readonly Dictionary<Type, ModuleDraft> _modules = [];

    public BrainCompositionBuilder WithModule<TModule>(Action<ModuleConfiguration<TModule>>? configure = null)
        where TModule : ModuleContract, new()
    {
        Declare<TModule>(out var draft);
        configure?.Invoke(new(draft));
        return this;
    }

    public BrainCompositionBuilder WithModule<TModule, TOptions>(Action<TOptions>? configureOptions = null,
        Action<ModuleConfiguration<TModule>>? configure = null)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        Declare<TModule>(out var draft);
        if (configureOptions is not null) { draft.Options!.Edit(configureOptions); }
        configure?.Invoke(new(draft));
        return this;
    }

    // Adds missing module requirements; explicitly declared modules keep their configuration.
    public BrainCompositionBuilder RequireModules(IEnumerable<ModuleContract> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules.Where(module => !_modules.ContainsKey(module.GetType())))
        { _modules.Add(module.GetType(), new ModuleDraft(module)); }
        return this;
    }

    public BrainCompositionBuilder ConfigureModule<TModule>(Action<ModuleConfiguration<TModule>> configure)
        where TModule : ModuleContract, new()
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new(RequireDeclared<TModule>()));
        return this;
    }

    public BrainCompositionBuilder ConfigureModule<TModule, TOptions>(Action<TOptions> configureOptions)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        RequireDeclared<TModule>().Options!.Edit(configureOptions);
        return this;
    }

    public BrainComposition Build()
    {
        var modules = ModuleComposition.Resolve(_modules.Values.Select(m => m.Compile()).ToArray());
        ModuleSettingsValidation.ValidatePublicSettings(modules);
        return new(modules, _modules.Values.SelectMany(m => m.LocalServices).ToArray());
    }

    private void Declare<TModule>(out ModuleDraft draft) where TModule : ModuleContract, new()
    {
        if (_modules.ContainsKey(typeof(TModule)))
        { throw new InvalidOperationException($"{typeof(TModule).Name} is already declared. Use ConfigureModule to change it."); }
        draft = new ModuleDraft(new TModule());
        _modules.Add(typeof(TModule), draft);
    }

    private ModuleDraft RequireDeclared<TModule>()
        => _modules.TryGetValue(typeof(TModule), out var draft) ? draft
            : throw new InvalidOperationException($"{typeof(TModule).Name} is not declared in this application.");
}

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

public sealed class ModuleConfiguration<TModule> where TModule : ModuleContract, new()
{
    private readonly ModuleDraft _draft;
    internal ModuleConfiguration(ModuleDraft draft) => _draft = draft;

    public void ConfigureLocalServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _draft.LocalServices.Add(configure);
    }
}

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

internal abstract class ModuleOptionsSlot
{
    public abstract Type OptionsType { get; }
    public abstract ModuleDefinition Compile();
    public abstract ModuleOptionsSlot Clone();
    public abstract void Edit(Delegate configure);

    public static ModuleOptionsSlot? For(Type moduleType)
    {
        var optionsType = OptionsTypeOf(moduleType);
        return optionsType is null ? null
            : (ModuleOptionsSlot)Activator.CreateInstance(typeof(ModuleOptionsSlot<,>).MakeGenericType(moduleType, optionsType), [null])!;
    }

    // Options live on the Module<TOptions> base; a settings-free module derives Module directly.
    private static Type? OptionsTypeOf(Type moduleType)
    {
        for (var type = moduleType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Module<>))
            { return type.GetGenericArguments()[0]; }
        }
        return null;
    }
}

internal sealed class ModuleOptionsSlot<TModule, TOptions> : ModuleOptionsSlot
    where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
{
    private TOptions _options;
    public ModuleOptionsSlot(TOptions? options) => _options = options ?? new TOptions();
    public override Type OptionsType => typeof(TOptions);
    public override ModuleDefinition Compile() => ModuleOptionsSerialization.Compile<TModule, TOptions>(_options);
    public override ModuleOptionsSlot Clone() => new ModuleOptionsSlot<TModule, TOptions>(RoundTrip(_options));

    public override void Edit(Delegate configure)
    {
        var working = RoundTrip(_options);
        ((Action<TOptions>)configure)(working);
        _options = RoundTrip(working);
    }

    private static TOptions RoundTrip(TOptions options)
        => JsonSerializer.Deserialize<TOptions>(JsonSerializer.Serialize(options))!;
}
