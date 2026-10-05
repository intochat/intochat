using Orleans.Hosting;

namespace DigitalBrain.Framework;

public abstract class Module
{
    protected Module(ModuleId id) => Id = id;
    public ModuleId Id { get; }
    public abstract void Configure(ISiloBuilder silo);
}

public abstract class Module<TOptions> : Module where TOptions : class, IModuleOptions, new()
{
    protected Module(ModuleId id) : base(id) { }
}
