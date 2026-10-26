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
    internal const string ManifestName = "playwright";

    // R4-04: what the module needs from its host (PlaywrightNeurons); its silo services stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => PlaywrightNeurons.Manifest;

    // The browser and page tools are plain neurons (06-06d) on the runtime's host grains.
    public override Type? Neurons => typeof(PlaywrightNeurons);

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

// The DigitalBrain module of Playwright (06-06d): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(PlaywrightModule.ManifestName)]
public sealed class PlaywrightNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<PlaywrightModuleOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(PlaywrightModule.ManifestName))],
        Secrets: []);
}
