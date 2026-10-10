using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Aspire.Server;

public sealed class DigitalBrainServerBuilder
{
    private readonly Dictionary<string, Module> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<IServiceCollection>> _serviceActions = [];
    internal bool Dashboard { get; private set; }
    public DigitalBrainServerBuilder AddModule<TModule>() where TModule : Module, new()
        => AddModule(new TModule());
    public DigitalBrainServerBuilder AddModule(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var id = module.Id.ToString();
        if (!_modules.TryAdd(id, module)) { throw new InvalidOperationException($"Module '{id}' is already registered."); }
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

    public DigitalBrainServerBuilder UseOrleansDashboard() { Dashboard = true; return this; }

    // Registered modules run; configuration can only subtract (Enabled=false) — and a
    // configuration that affirms a module this server never registered is a deployment bug.
    internal Module[] SelectModules(IConfiguration configuration)
    {
        // Configuration keys are case-insensitive, so the ids match that way too.
        var sections = configuration.GetSection(DigitalBrainConfiguration.ModulesSectionName).GetChildren().ToArray();
        var registered = _modules.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            if (section.GetValue<bool?>("Enabled") == true && !registered.Contains(section.Key))
            { throw new InvalidOperationException($"Selected module '{section.Key}' is not registered in this server."); }
        }
        var disabled = sections.Where(section => section.GetValue<bool?>("Enabled") == false)
            .Select(section => section.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _modules.Where(pair => !disabled.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
    }
}
