using DigitalBrain;
using DigitalBrain.Framework;
using global::Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using DigitalBrain.Framework.Modules;
namespace DigitalBrain.Microsoft.Playwright;

public sealed class PlaywrightModule() : Module(new ModuleId("playwright"))
{
    public override void Configure(ISiloBuilder silo) => silo.AddPlaywright();
}
public static class PlaywrightHosting
{
    public static ISiloBuilder AddPlaywright(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<IBrowserSessionProvider, PlaywrightSessionProvider>();
        return silo;
    }
}
