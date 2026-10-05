using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Registry;

public sealed class RegistryModule() : Module(new ModuleId("registry"))
{
    public const string Key = IRegistry.Key;
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<RegistryDiscoveryService>();
    }
}
