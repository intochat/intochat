using System.Reflection;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Modules;

// The host's view of one composed module: a Framework module instance, or a [Module] DigitalBrain module.
// Callers read id, contracts and topology from here. The module class itself is not a service locator.
public sealed class OsModule
{
    private OsModule(Type type, ModuleId id, bool requiresContainer, int order, string? licenseProduct,
        Assembly? contracts, Type? neurons, IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> topologyProfiles,
        IReadOnlyList<IntegrationSchema>? integrationSchemas = null)
    {
        Type = type;
        Id = id;
        RequiresContainer = requiresContainer;
        Order = order;
        LicenseProduct = licenseProduct;
        Contracts = contracts;
        Neurons = neurons;
        TopologyProfiles = topologyProfiles;
        IntegrationSchemas = integrationSchemas ?? [];
    }

    public Type Type { get; }
    public ModuleId Id { get; }
    public bool RequiresContainer { get; }
    public int Order { get; }
    public string? LicenseProduct { get; }
    public Assembly? Contracts { get; }
    public Type? Neurons { get; }
    public IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> TopologyProfiles { get; }
    public IReadOnlyList<IntegrationSchema> IntegrationSchemas { get; init; } = [];
    private object? _runtime;

    // ConfigureServices and the HTTP surface share one instance. A DigitalBrain module has no host-held instance until first use.
    private object Runtime() => _runtime ??= Activator.CreateInstance(Type)!;

    public static OsModule WithContracts(string id, Assembly contracts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(contracts);
        return new OsModule(typeof(OsModule), new ModuleId(id), false, 0, null, contracts, null, []);
    }

    public static OsModule FromDigital(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var manifest = DigitalBrain.ModuleManifest.Of(type);
        var id = new ModuleId(manifest.Id ?? throw new InvalidOperationException($"{type.FullName} has no module id."));
        var requiresContainer = Flag(type, "RequiresContainer")
            || manifest.Needs.Any(need => need.Kind == DigitalBrain.NeedKind.Sandbox && need.Image is not null);
        return new OsModule(type, id, requiresContainer, Number(type, "Order"), Text(type, "LicenseProduct"),
            ContractsOf(type), NeuronsOf(type), Profiles(type), ReadSchemas(type));
    }

    public static IReadOnlyList<IntegrationSchema> ReadSchemas(Type module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return [.. Declared(module, "Integration"), .. Declared(module, "Integrations")];
    }

    // Absent LoadsUnder means the module loads under every topology. A cloud module returns false for local.
    public bool LoadsUnder(string? topology)
    {
        var method = Type.GetMethod("LoadsUnder", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, [typeof(string)]);
        if (method is null || method.ReturnType != typeof(bool)) { return true; }
        return (bool)method.Invoke(null, [topology])!;
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (Type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null) { return; }
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var two = Type.GetMethod("ConfigureServices", Flags, [typeof(IServiceCollection), typeof(IConfiguration)]);
        if (two is not null)
        {
            two.Invoke(two.IsStatic ? null : Runtime(), [services, configuration]);
            return;
        }
        var one = Type.GetMethod("ConfigureServices", Flags, [typeof(IServiceCollection)]);
        if (one is null) { return; }
        one.Invoke(one.IsStatic ? null : Runtime(), [services]);
    }

    public void BindOptions(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null) { return; }
        foreach (var setting in DigitalBrain.ModuleManifest.Of(Type).Settings)
        {
            typeof(OsModule).GetMethod(nameof(Bind), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(setting.OptionsType).Invoke(null, [services, setting.Section, Id.Value]);
        }
    }

    private static void Bind<TOptions>(IServiceCollection services, string section, string moduleId) where TOptions : class
    {
        var builder = services.AddOptions<TOptions>().BindConfiguration(section);
        if (!typeof(IModuleOptions).IsAssignableFrom(typeof(TOptions))) { return; }
        builder.Validate(options =>
        {
            Validate(options, moduleId);
            return true;
        }).ValidateOnStart();
    }

    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null || !typeof(IHttpModule).IsAssignableFrom(Type)) { return; }
        var instance = Runtime();
        services.AddSingleton(Type, instance);
        services.AddSingleton(typeof(IHttpModule), instance);
    }

    // Neuron modules the catalog does not already represent (authority, confirmations, a test WithNeuronModule).
    public static void HostIndependents(IServiceCollection services, IConfiguration configuration, IEnumerable<Type> neuronModules, IEnumerable<OsModule> selected)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(neuronModules);
        ArgumentNullException.ThrowIfNull(selected);
        var owned = selected.Select(module => module.Type).Concat(selected.Select(module => module.Neurons).OfType<Type>()).ToHashSet();
        foreach (var type in neuronModules.Distinct())
        {
            if (!owned.Add(type) || !typeof(DigitalBrain.Module).IsAssignableFrom(type)) { continue; }
            if (type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null) { continue; }
            var hosted = FromDigital(type);
            hosted.BindOptions(services);
            hosted.ConfigureServices(services, configuration);
            hosted.Register(services);
        }
    }

    private static void Validate(object options, string moduleId)
        => typeof(ModuleOptionsSerialization).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(ModuleOptionsSerialization.Validate) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(options.GetType()).Invoke(null, [options, moduleId]);

    private static bool Flag(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) is true;

    private static int Number(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) is int value ? value : 0;

    private static string? Text(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as string;

    private static Assembly? ContractsOf(Type type)
        => type.GetProperty("Contracts", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as Assembly;

    // A module may name a different [Module] type whose assembly holds its neurons. Absent means this type.
    private static Type NeuronsOf(Type type)
        => type.GetProperty("Neurons", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as Type ?? type;

    private static IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> Profiles(Type type)
    {
        if (typeof(ITopologyModule).IsAssignableFrom(type))
        { return ((ITopologyModule)Activator.CreateInstance(type)!).TopologyProfiles; }
        return type.GetProperty("TopologyProfiles", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
            as IReadOnlyList<DigitalBrain.Runtime.Orleans.ITopologyProfile> ?? [];
    }

    private static IEnumerable<IntegrationSchema> Declared(Type module, string member)
    {
        const BindingFlags Static = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var value = module.GetProperty(member, Static)?.GetValue(null) ?? module.GetField(member, Static)?.GetValue(null);
        return value switch
        {
            IntegrationSchema single => [single],
            IEnumerable<IntegrationSchema> many => many,
            _ => [],
        };
    }
}
