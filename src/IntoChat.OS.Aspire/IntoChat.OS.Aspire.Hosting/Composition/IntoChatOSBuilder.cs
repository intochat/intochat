using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Orleans;
using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

public sealed class IntoChatOSBuilder
{
    private readonly Dictionary<string, IConfiguration> _compiledConfiguration = new(StringComparer.Ordinal);
    public IConfiguration GetModuleConfiguration(string id)
        => _compiledConfiguration.TryGetValue(id, out var configuration) ? configuration : ApplicationBuilder.Configuration;

    public IntoChatOSBuilder WithModule(Module module, IIntoChatOSModuleHosting? hosting = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Register(module.Id, hosting, settings);
    }

    public IntoChatOSBuilder WithHosting(IIntoChatOSModuleHosting hosting, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        return Register(hosting.Module, hosting, settings);
    }

    public IntoChatOSBuilder WithHosting<THosting>() where THosting : IIntoChatOSModuleHosting, new()
        => WithHosting(new THosting());

    public IntoChatOSBuilder WithHosting<THosting, TOptions>(Action<TOptions>? configure = null)
        where THosting : IIntoChatOSModuleHosting, new() where TOptions : class, IntoChat.OS.Framework.Modules.IModuleOptions, new()
    {
        var hosting = new THosting();
        var options = new TOptions();
        configure?.Invoke(options);
        options.Validate();
        return WithHosting(hosting, HostingModuleOptions.Flatten(options, hosting.Module.ToString()));
    }

    public IntoChatOSBuilder WithHosting<THosting, TOptions>(TOptions options)
        where THosting : IIntoChatOSModuleHosting, new()
        where TOptions : class, IntoChat.OS.Framework.Modules.IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        var hosting = new THosting();
        options.Validate();
        return WithHosting(hosting, HostingModuleOptions.Flatten(options, hosting.Module.ToString()));
    }

    // A module the deployment configures but does not host (no containers): its typed options only.
    public IntoChatOSBuilder WithModule<TModule, TOptions>(Action<TOptions> configure)
        where TModule : Module, new() where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(configure);
        var module = new TModule();
        var options = new TOptions();
        configure(options);
        options.Validate();
        return WithModule(module, null, HostingModuleOptions.Flatten(options, module.Id.ToString()));
    }

    private IntoChatOSBuilder Register(ModuleId id, IIntoChatOSModuleHosting? hosting,
        IReadOnlyDictionary<string, string?>? settings)
    {
        var moduleId = id.ToString();
        if (_modules.Contains(moduleId, StringComparer.Ordinal)) { throw new InvalidOperationException($"Module '{moduleId}' is already selected."); }
        var replaceOptions = ApplicationBuilder.Configuration.GetValue<bool>(IntoChatOSConfiguration.ModuleSection(id) + ":ReplaceOptions");
        var values = new Dictionary<string, string?>(replaceOptions ? [] : settings ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ApplicationBuilder.Configuration.GetSection(IntoChatOSConfiguration.ModuleOptionsSection(id)).AsEnumerable())
        { if (pair.Value is not null) { values[pair.Key] = pair.Value; } }
        var scopedPrefix = IntoChatOSConfiguration.CompositionModuleOptionsSection(Name, id);
        foreach (var pair in ApplicationBuilder.Configuration.GetSection(scopedPrefix).AsEnumerable())
        {
            if (pair.Value is not null) { values[IntoChatOSConfiguration.ModuleOptionsSection(id) + pair.Key[scopedPrefix.Length..]] = pair.Value; }
        }
        _compiledConfiguration[moduleId] = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        _modules.Add(moduleId);
        Resource.WithAnnotation(new BrainModuleAnnotation(moduleId));
        GetOrAddModuleNode(moduleId);
        foreach (var pair in values) { _settings[pair.Key] = pair.Value; }
        hosting?.Configure(this);
        return this;
    }

    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, string?> Settings => _settings;
    public IntoChat.OS.Authority.Identity.Configuration.IdentityHostOptions? Identity { get; private set; }
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
