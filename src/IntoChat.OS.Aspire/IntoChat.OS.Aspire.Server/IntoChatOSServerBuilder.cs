using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace IntoChat.OS.Aspire.Server;

public sealed class IntoChatOSServerBuilder
{
    private readonly Dictionary<string, Module> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<IServiceCollection>> _serviceActions = [];
    private ILicenseCheck? _licenseCheck;
    internal bool Dashboard { get; private set; }
    public IntoChatOSServerBuilder AddModule<TModule>() where TModule : Module, new()
        => AddModule(new TModule());
    public IntoChatOSServerBuilder AddModule(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var id = module.Id.ToString();
        if (!_modules.TryAdd(id, module)) { throw new InvalidOperationException($"Module '{id}' is already registered."); }
        return this;
    }
    // A composition carries the host services it cannot run without (the OS carries its apps);
    // the hosting extension applies these to the host's service collection.
    public IntoChatOSServerBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceActions.Add(configure);
        return this;
    }

    internal void ApplyServiceActions(IServiceCollection services)
    {
        foreach (var action in _serviceActions) { action(services); }
    }

    // Without a check, a module that declares a LicenseProduct is unlicensed.
    public IntoChatOSServerBuilder UseLicenseCheck(ILicenseCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _licenseCheck = check;
        return this;
    }

    public IntoChatOSServerBuilder UseOrleansDashboard() { Dashboard = true; return this; }

    // Registered modules run; configuration can only subtract (Enabled=false) — and a
    // configuration that affirms a module this server never registered is a deployment bug.
    // A module with a LicenseProduct also needs a valid licence: without one it is subtracted and
    // reported Unlicensed, unless IntoChatOS:Modules:{id}:Required is true, which refuses to start.
    internal ModuleSelection SelectModules(IConfiguration configuration)
    {
        // Configuration keys are case-insensitive, so the ids match that way too.
        var sections = configuration.GetSection(IntoChatOSConfiguration.ModulesSectionName).GetChildren().ToArray();
        var registered = _modules.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            if (section.GetValue<bool?>("Enabled") == true && !registered.Contains(section.Key))
            { throw new InvalidOperationException($"Selected module '{section.Key}' is not registered in this server."); }
        }
        var disabled = sections.Where(section => section.GetValue<bool?>("Enabled") == false)
            .Select(section => section.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modules = new List<Module>();
        var statuses = new List<ModuleStatus>();
        foreach (var (key, module) in _modules)
        {
            if (disabled.Contains(key)) { statuses.Add(new ModuleStatus(module.Id, ModuleState.Disabled, null)); continue; }
            if (module.LicenseProduct is { } product && Unlicensed(module, product, configuration) is { } reason)
            {
                if (configuration.GetValue<bool?>(IntoChatOSConfiguration.ModuleSection(key) + ":Required") == true)
                { throw new InvalidOperationException($"Module '{key}' is required but unlicensed: {reason}"); }
                statuses.Add(new ModuleStatus(module.Id, ModuleState.Unlicensed, reason));
                continue;
            }
            modules.Add(module);
            statuses.Add(new ModuleStatus(module.Id, ModuleState.Active, null));
        }
        return new ModuleSelection([.. modules], [.. statuses]);
    }

    // The reasons are a closed set so a check cannot leak a token through them.
    private string? Unlicensed(Module module, string product, IConfiguration configuration)
    {
        var result = _licenseCheck?.Check(module.Id, new LicenseContext(configuration, product))
            ?? new LicenseResult(false, null, null, "missing");
        if (result.Valid) { return null; }
        return "license: " + (result.Reason is "missing" or "invalid" or "expired" ? result.Reason : "invalid");
    }
}

internal readonly record struct ModuleSelection(Module[] Modules, ModuleStatus[] Statuses);
