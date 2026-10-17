using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using global::Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
namespace DigitalBrain.Microsoft.Playwright;

public sealed class PlaywrightModule() : Module<PlaywrightModuleOptions>(new ModuleId("playwright"))
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Microsoft.Playwright.IPlaywright).Assembly;

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
