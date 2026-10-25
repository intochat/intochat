using System.Reflection;
using IntoChat.OS.Framework.Configuration;

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

    public abstract void Configure(ISiloBuilder silo);
}

// A module with options gets them bound by the host: IOptions<TOptions> reads IntoChatOS:Modules:{id}:Options
// and validates at host startup. Configure may chain further validation on services.AddOptions<TOptions>().
public abstract class Module<TOptions> : Module where TOptions : class, IModuleOptions, new()
{
    protected Module(ModuleId id) : base(id) { }
}
