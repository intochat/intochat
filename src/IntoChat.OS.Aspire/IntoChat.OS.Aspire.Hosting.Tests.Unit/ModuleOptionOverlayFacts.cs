using Aspire.Hosting;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Orleans.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ModuleOptionOverlayFacts
{
    [DigitalBrain.Module("example")]
    private sealed class ExampleModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    [DigitalBrain.Module("OverlaidModule")]
    private sealed class OverlaidModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    [DigitalBrain.Module("scoped")]
    private sealed class ScopedModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    public sealed class OverlaidOptions : IModuleOptions
    {
        public string Endpoint { get; set; } = "default";
        public int Delay { get; set; } = 7;
        public Dictionary<string, string> Bindings { get; set; } = [];
        public void Validate() { }
    }

    [Fact]
    public void ReplacementOptionsRemoveCodeDeclaredCollectionsAndRestoreTypeDefaults()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IntoChatOSConfiguration.ModuleSection("example") + ":ReplaceOptions"] = "true";
        builder.Configuration[IntoChatOSConfiguration.ModuleOptionsSection("example") + ":Endpoint"] = "fixture";
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithModule(new ExampleModule(), settings: HostingModuleOptions.Flatten(new OverlaidOptions
            {
                Endpoint = "production",
                Delay = 42,
                Bindings = new() { ["live"] = "service" }
            }, "example"));
        var options = brain.GetModuleConfiguration("example").GetModuleOptions<OverlaidOptions>("example");
        Assert.Equal("fixture", options.Endpoint);
        Assert.Equal(7, options.Delay);
        Assert.Empty(options.Bindings);
    }

    [Fact]
    public void FlatteningOmitsDefaultsSoDeploymentCodeDoesNotPinThem()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithModule(new ExampleModule(), settings: HostingModuleOptions.Flatten(new OverlaidOptions { Endpoint = "production" }, "example"));
        var configuration = brain.GetModuleConfiguration("example");
        Assert.Equal("production", configuration[IntoChatOSConfiguration.ModuleOptionsSection("example") + ":Endpoint"]);
        // Delay was left at its default: the deployment never decided it, so the silo's own
        // configuration must stay free to.
        Assert.Null(configuration[IntoChatOSConfiguration.ModuleOptionsSection("example") + ":Delay"]);
    }

    [Fact]
    public void TwoBrainsScopeInfrastructureAndAllowIndependentConfiguration()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IntoChatOSConfiguration.CompositionModuleOptionsSection("first", "scoped") + ":ConnectionName"] = "first-db";
        builder.Configuration[IntoChatOSConfiguration.CompositionModuleOptionsSection("second", "scoped") + ":ConnectionName"] = "second-db";
        var first = builder.AddIntoChatOS("first", options: new())
            .WithHosting<ScopedServerHosting>();
        var second = builder.AddIntoChatOS("second", options: new())
            .WithHosting<ScopedServerHosting>();
        Assert.Equal("first-db", first.GetModuleConfiguration("scoped")[IntoChatOSConfiguration.ModuleOptionsSection("scoped") + ":ConnectionName"]);
        Assert.Equal("second-db", second.GetModuleConfiguration("scoped")[IntoChatOSConfiguration.ModuleOptionsSection("scoped") + ":ConnectionName"]);
        var names = builder.Resources.Select(r => r.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("first-scoped-server", names);
        Assert.Contains("second-scoped-server", names);
    }

    private sealed class ScopedServerHosting : ModuleHosting<ScopedModule>
    {
        public override void Configure(IntoChatOSBuilder brain)
            => brain.ApplicationBuilder.AddContainer(brain.ResourceName("scoped-server"), "scoped/server");
    }

    [Fact]
    public void The_app_hosts_own_configuration_overrides_a_code_declared_module_option()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args =
            [
                IntoChatOSConfiguration.ModuleOptionsSection("OverlaidModule") + ":Endpoint=from-args",
                IntoChatOSConfiguration.ModuleOptionsSection("OverlaidModule") + ":Bindings:extra=added",
            ],
            DisableDashboard = true,
        });
        var brain = builder.AddIntoChatOS("modules", options: new())
            .WithModule(new OverlaidModule(), settings: HostingModuleOptions.Flatten(new OverlaidOptions { Endpoint = "declared", Delay = 42 }, "OverlaidModule"));

        var options = brain.GetModuleConfiguration("OverlaidModule").GetModuleOptions<OverlaidOptions>("OverlaidModule");
        Assert.Equal("from-args", options.Endpoint);
        Assert.Equal(42, options.Delay);
        Assert.Equal("added", options.Bindings["extra"]);
    }
}
