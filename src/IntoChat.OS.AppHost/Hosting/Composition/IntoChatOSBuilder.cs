using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Orleans;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public sealed class IntoChatOSBuilder
{
    private readonly Dictionary<string, IConfiguration> _compiledConfiguration = new(StringComparer.Ordinal);
    public IConfiguration GetModuleConfiguration(string id)
        => _compiledConfiguration.TryGetValue(id, out var configuration) ? configuration : ApplicationBuilder.Configuration;

    public IntoChatOSBuilder WithModule(DigitalBrain.Modules.Module module, IIntoChatOSModuleHosting? hosting = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Register(HostedModuleId.Of(module.GetType()), module.GetType(), hosting, settings);
    }

    public IntoChatOSBuilder WithModule<TModule>() where TModule : class, new()
        => Register(HostedModuleId.Of(typeof(TModule)), typeof(TModule), null, null);

    public IntoChatOSBuilder WithHosting(IIntoChatOSModuleHosting hosting, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        return Register(hosting.Module, null, hosting, settings);
    }

    public IntoChatOSBuilder WithHosting<THosting>() where THosting : IIntoChatOSModuleHosting, new()
        => WithHosting(new THosting());

    public IntoChatOSBuilder WithHosting<THosting, TOptions>(Action<TOptions>? configure = null)
        where THosting : IIntoChatOSModuleHosting, new() where TOptions : class, DigitalBrain.Modules.Options.IModuleOptions, new()
    {
        var hosting = new THosting();
        var options = new TOptions();
        configure?.Invoke(options);
        ModuleOptionsSerialization.Validate(options, hosting.Module.ToString());
        return WithHosting(hosting, HostingModuleOptions.Flatten(options, hosting.Module.ToString()));
    }

    public IntoChatOSBuilder WithHosting<THosting, TOptions>(TOptions options)
        where THosting : IIntoChatOSModuleHosting, new()
        where TOptions : class, DigitalBrain.Modules.Options.IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        var hosting = new THosting();
        ModuleOptionsSerialization.Validate(options, hosting.Module.ToString());
        return WithHosting(hosting, HostingModuleOptions.Flatten(options, hosting.Module.ToString()));
    }

    // A module the deployment configures: its typed options and manifest-driven infrastructure.
    public IntoChatOSBuilder WithModule<TModule, TOptions>(Action<TOptions> configure)
        where TModule : class, new() where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(configure);
        var id = HostedModuleId.Of(typeof(TModule));
        var options = new TOptions();
        configure(options);
        ModuleOptionsSerialization.Validate(options, id.ToString());
        return Register(id, typeof(TModule), null, HostingModuleOptions.Flatten(options, id.ToString()));
    }

    public IntoChatOSBuilder WithModule<TModule, TOptions>(TOptions options)
        where TModule : class, new() where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        var id = HostedModuleId.Of(typeof(TModule));
        ModuleOptionsSerialization.Validate(options, id.ToString());
        return Register(id, typeof(TModule), null, HostingModuleOptions.Flatten(options, id.ToString()));
    }

    private IntoChatOSBuilder Register(ModuleId id, Type? moduleType, IIntoChatOSModuleHosting? hosting,
        IReadOnlyDictionary<string, string?>? settings)
    {
        var moduleId = id.ToString();
        if (_modules.Contains(moduleId, StringComparer.Ordinal)) { throw new InvalidOperationException($"Module '{moduleId}' is already selected."); }
        var replaceOptions = ApplicationBuilder.Configuration.GetValue<bool>(ModuleConfiguration.ModuleSection(id) + ":ReplaceOptions");
        var values = new Dictionary<string, string?>(replaceOptions ? [] : settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ApplicationBuilder.Configuration.GetSection(ModuleConfiguration.ModuleOptionsSection(id)).AsEnumerable())
        { if (pair.Value is not null) { values[pair.Key] = pair.Value; } }
        var scopedPrefix = ModuleConfiguration.CompositionModuleOptionsSection(Name, id);
        foreach (var pair in ApplicationBuilder.Configuration.GetSection(scopedPrefix).AsEnumerable())
        {
            if (pair.Value is not null) { values[ModuleConfiguration.ModuleOptionsSection(id) + pair.Key[scopedPrefix.Length..]] = pair.Value; }
        }
        _compiledConfiguration[moduleId] = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        _modules.Add(moduleId);
        Resource.WithAnnotation(new BrainModuleAnnotation(moduleId));
        GetOrAddModuleNode(moduleId);
        foreach (var pair in values) { _settings[pair.Key] = pair.Value; }
        if (moduleType is not null)
        {
            ModuleSecretProjection.Project(this, moduleType);
            ManifestHostingProjector.Project(this, moduleType);
        }
        hosting?.Configure(this);
        return this;
    }

    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, string?> Settings => _settings;
    public IntoChat.OS.Modules.Identity.Contracts.Accounts.IdentityHostOptions? Identity { get; private set; }
    public IntoChatOSBuilder WithHttpIdentity()
    {
        Identity = HttpIdentityProjection.FromConfiguration(ApplicationBuilder.Configuration);
        return this;
    }
    private readonly List<string> _modules = [];
    private readonly List<IntoChatOSModuleProjection> _projections = [];
    private readonly List<IResource> _startupDependencies = [];
    private readonly Dictionary<Type, object> _states = [];

    private readonly Dictionary<string, IResourceBuilder<IntoChatOSModuleResource>> _moduleNodes = new(StringComparer.Ordinal);

    internal IntoChatOSBuilder(
        IDistributedApplicationBuilder builder, string name,
        IResourceBuilder<IntoChatOSResource> resource, OrleansService orleans)
    {
        ApplicationBuilder = builder;
        Name = name;
        Resource = resource;
        Orleans = orleans;
    }

    public string Name { get; }
    public string ServiceId { get; internal init; } = null!;
    public string ClusterId { get; internal init; } = null!;
    public string GrainStateContainer { get; internal init; } = null!;
    public string? Environment { get; internal init; }
    public string? ClusteringResourceName { get; set; }
    public DeploymentProfile? Profile { get; internal set; }

    public string ResourceName(string name) => $"{Name}-{name}";

    public IDistributedApplicationBuilder ApplicationBuilder { get; }

    public IResourceBuilder<IntoChatOSResource> Resource { get; }

    public IResourceBuilder<AzureBlobStorageResource>? GrainState { get; set; }

    public IResourceBuilder<AzureStorageResource>? Storage { get; set; }

    public OrleansService Orleans { get; }
    public IResourceBuilder<AzureTableStorageResource>? Clustering { get; set; }
    public IResourceBuilder<AzureTableStorageResource>? Reminders { get; set; }

    internal IReadOnlyList<IntoChatOSModuleProjection> Projections => _projections;

    internal IReadOnlyList<IResource> StartupDependencies => _startupDependencies;

    internal IReadOnlyList<string> Modules => _modules;

    public TState GetOrAddState<TState>(Func<IntoChatOSBuilder, TState> create, out bool added)
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(create);

        if (_states.TryGetValue(typeof(TState), out var existing))
        {
            added = false;
            return (TState)existing;
        }

        var state = create(this);
        _states.Add(typeof(TState), state);
        added = true;
        return state;
    }

    internal bool TryGetState<TState>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TState? state)
        where TState : class
    {
        if (_states.TryGetValue(typeof(TState), out var existing))
        {
            state = (TState)existing;
            return true;
        }

        state = null;
        return false;
    }

    public void AddProjection(IntoChatOSModuleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (_projections.Any(existing => existing.GetType() == projection.GetType()))
        {
            throw new InvalidOperationException(
                $"{projection.GetType().Name} is already configured on brain '{Name}'. Add it exactly once.");
        }

        _projections.Add(projection);
    }

    public void RequireHealthyBeforeStart(IResource dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        if (!_startupDependencies.Contains(dependency))
        {
            _startupDependencies.Add(dependency);
        }
    }

    public IntoChatOSClientReference AsClient(bool shareHttpIdentity = false) => new(this, shareHttpIdentity);

    internal bool HasResource(string name)
        => ApplicationBuilder.Resources.Any(resource =>
            string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase));

    public IResourceBuilder<IntoChatOSModuleResource> GetOrAddModuleNode(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (_moduleNodes.TryGetValue(displayName, out var existing))
        {
            return existing;
        }

        var node = ApplicationBuilder.AddResource(new IntoChatOSModuleResource(ResourceName(displayName)))
            .ExcludeFromManifest();
        node.WithParentRelationship(Resource);
        node.WithInitialState(new CustomResourceSnapshot
        {
            ResourceType = "Module",
            CreationTimeStamp = DateTime.UtcNow,
            State = KnownResourceStates.Running,
            Properties = [new(CustomResourceKnownProperties.Source, displayName)],
        });
        _moduleNodes.Add(displayName, node);
        return node;
    }
}
