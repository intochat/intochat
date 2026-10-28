using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework.Modules.Configuration;

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

    // True when hosting this module needs a container (a database, a vector store, images); an
    // in-process test brain composes only the modules that need none.
    public virtual bool RequiresContainer => false;

    // Position in the catalog: lower first, ties broken by id. Orleans is negative so it comes first.
    public virtual int Order => 0;

    // Transitional (06-03, until Configure leaves in 06-08): the DigitalBrain module ([Module] class with a static
    // ModuleManifest) whose plain [Neuron] classes this module brings. The host passes it to UseModuleTopology, so its
    // neurons run on the runtime's host grains and its Needs are checked against the topology.
    public virtual Type? Neurons => null;

    // The module's service registrations, on the host's services (06-08: replaces the silo-builder hook; the DigitalBrain
    // manifest declares what the module provides and needs).
    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }
}

// A module with options gets them bound by the host: IOptions<TOptions> reads IntoChatOS:Modules:{id}:Options
// and validates at host startup. ConfigureServices may chain further validation on services.AddOptions<TOptions>().
public abstract class Module<TOptions> : Module where TOptions : class, IModuleOptions, new()
{
    protected Module(ModuleId id) : base(id) { }
}
