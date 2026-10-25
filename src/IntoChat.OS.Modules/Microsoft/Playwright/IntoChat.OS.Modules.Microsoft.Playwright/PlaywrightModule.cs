using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using global::Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.Playwright;

public sealed class PlaywrightModule() : Module<PlaywrightModuleOptions>(new ModuleId("playwright"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Microsoft.Playwright.IPlaywright).Assembly;

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
