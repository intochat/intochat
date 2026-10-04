using DigitalBrain.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Aspire.Server;

public sealed class DigitalBrainServerBuilder
{
    private readonly Dictionary<string, Func<IModule>> _modules = new(StringComparer.Ordinal);
    private readonly List<Action<IServiceCollection>> _serviceActions = [];
    internal bool AzureStorage { get; private set; }
    internal bool Dashboard { get; private set; }
    public DigitalBrainServerBuilder AddModule<TModule>() where TModule : class, IModule, new()
        => AddModule(typeof(TModule));
    public DigitalBrainServerBuilder AddModule(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        if (!typeof(IModule).IsAssignableFrom(moduleType) || moduleType.IsAbstract || moduleType.GetConstructor(Type.EmptyTypes) is null)
        { throw new ArgumentException("Select a concrete module with a public parameterless constructor.", nameof(moduleType)); }
        return AddModule(Contracts.ModuleIdentity.Get(moduleType), () => (IModule)Activator.CreateInstance(moduleType)!);
    }
    public DigitalBrainServerBuilder AddModule(string id, Func<IModule> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(create);
        if (!_modules.TryAdd(id, create)) { throw new InvalidOperationException($"Module '{id}' is already registered."); }
        return this;
    }
    // A composition carries the host services it cannot run without (the OS carries its apps);
    // the hosting extension applies these to the host's service collection.
    public DigitalBrainServerBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceActions.Add(configure);
        return this;
    }

    internal void ApplyServiceActions(IServiceCollection services)
    {
        foreach (var action in _serviceActions) { action(services); }
    }

    public DigitalBrainServerBuilder UseAzureStorage() { AzureStorage = true; return this; }
    public DigitalBrainServerBuilder WithDashboard() { Dashboard = true; return this; }
    // Registered modules run; configuration can only subtract (Enabled=false) — and a
    // configuration that affirms a module this server never registered is a deployment bug.
    internal IModule[] SelectModules(IConfiguration configuration)
    {
        // Configuration keys are case-insensitive, so the ids match that way too.
        var sections = configuration.GetSection("DigitalBrain:Modules").GetChildren().ToArray();
        var registered = _modules.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            if (section.GetValue<bool?>("Enabled") == true && !registered.Contains(section.Key))
            { throw new InvalidOperationException($"Selected module '{section.Key}' is not registered in this server."); }
        }
        var disabled = sections.Where(section => section.GetValue<bool?>("Enabled") == false)
            .Select(section => section.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _modules.Where(pair => !disabled.Contains(pair.Key))
            .Select(pair => pair.Value() ?? throw new InvalidOperationException($"Factory for '{pair.Key}' returned null.")).ToArray();
    }
}
