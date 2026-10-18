using System.Reflection;
using IntoChat.OS.Framework.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Modules;

public abstract class Module
{
    protected Module(ModuleId id) => Id = id;
    public ModuleId Id { get; }

    // The assembly whose exported neurons and signals this module puts on the program surface.
    // Composing the module is what exposes them; a module without one exposes nothing.
    public virtual Assembly? Contracts => null;

    // The product this module is licensed as; null for a module that needs no licence.
    public virtual string? LicenseProduct => null;

    public abstract void Configure(ISiloBuilder silo);

    internal virtual void AddOptions(IServiceCollection services) { }
}

// A module with options gets them bound for it: IOptions<TOptions> reads IntoChatOS:Modules:{id}:Options
// and validates on first use. Configure may chain further validation on services.AddOptions<TOptions>().
public abstract class Module<TOptions> : Module where TOptions : class, IModuleOptions, new()
{
    protected Module(ModuleId id) : base(id) { }

    internal sealed override void AddOptions(IServiceCollection services)
        => services.AddOptions<TOptions>()
            .BindConfiguration(IntoChatOSConfiguration.ModuleOptionsSection(Id))
            .Validate(options => { options.Validate(); return true; });
}
