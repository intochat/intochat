using Aspire.Hosting;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Contracts;
using Orleans.Hosting;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ModuleOptionOverlayFacts
{
    [ModuleId("example")]
    private sealed class ExampleModule : DigitalBrain.Kernel.IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    [ModuleId("OverlaidModule")]
    private sealed class OverlaidModule : DigitalBrain.Kernel.IModule
    { public void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

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
        builder.Configuration["DigitalBrain:Modules:example:ReplaceOptions"] = "true";
        builder.Configuration["DigitalBrain:Modules:example:Options:Endpoint"] = "fixture";
        var brain = builder.AddDigitalBrain("modules", new DigitalBrainHostingOptions { PersistentStorage = false })
            .WithModule(typeof(ExampleModule), settings: HostingModuleOptions.Flatten(new OverlaidOptions
            {
                Endpoint = "production",
                Delay = 42,
                Bindings = new() { ["live"] = "service" }
            }, "example"));
        var options = HostingModuleOptions.GetModuleOptions<OverlaidOptions>(brain.GetModuleConfiguration("example"), "example");
        Assert.Equal("fixture", options.Endpoint);
        Assert.Equal(7, options.Delay);
        Assert.Empty(options.Bindings);
    }

    [Fact]
    public void FlatteningOmitsDefaultsSoDeploymentCodeDoesNotPinThem()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("modules", new DigitalBrainHostingOptions { PersistentStorage = false })
            .WithModule(typeof(ExampleModule), settings: HostingModuleOptions.Flatten(new OverlaidOptions { Endpoint = "production" }, "example"));
        var configuration = brain.GetModuleConfiguration("example");
        Assert.Equal("production", configuration["DigitalBrain:Modules:example:Options:Endpoint"]);
        // Delay was left at its default: the deployment never decided it, so the silo's own
        // configuration must stay free to.
        Assert.Null(configuration["DigitalBrain:Modules:example:Options:Delay"]);
    }

    [Fact]
    public void TwoBrainsScopeInfrastructureAndAllowIndependentConfiguration()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration["DigitalBrain:Brains:first:Modules:scoped:Options:ConnectionName"] = "first-db";
        builder.Configuration["DigitalBrain:Brains:second:Modules:scoped:Options:ConnectionName"] = "second-db";
        var first = builder.AddDigitalBrain("first", options: new() { UseAzureStorage = true, PersistentStorage = false })
            .WithHosting<ScopedServerHosting>();
        var second = builder.AddDigitalBrain("second", options: new() { UseAzureStorage = true, PersistentStorage = false })
            .WithHosting<ScopedServerHosting>();
        Assert.Equal("first-db", first.GetModuleConfiguration("scoped")["DigitalBrain:Modules:scoped:Options:ConnectionName"]);
        Assert.Equal("second-db", second.GetModuleConfiguration("scoped")["DigitalBrain:Modules:scoped:Options:ConnectionName"]);
        var names = builder.Resources.Select(r => r.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("first-scoped-server", names);
        Assert.Contains("second-scoped-server", names);
    }

    private sealed class ScopedServerHosting : IDigitalBrainModuleHosting
    {
        public string Id => "scoped";
        public void Configure(DigitalBrainBuilder brain)
            => brain.ApplicationBuilder.AddContainer(brain.ResourceName("scoped-server"), "scoped/server");
    }

    [Fact]
    public void The_app_hosts_own_configuration_overrides_a_code_declared_module_option()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args =
            [
                "DigitalBrain:Modules:OverlaidModule:Options:Endpoint=from-args",
                "DigitalBrain:Modules:OverlaidModule:Options:Bindings:extra=added",
            ],
            DisableDashboard = true,
        });
        var brain = builder.AddDigitalBrain("modules", options: new() { UseAzureStorage = true, PersistentStorage = false })
            .WithModule(typeof(OverlaidModule), settings: HostingModuleOptions.Flatten(new OverlaidOptions { Endpoint = "declared", Delay = 42 }, "OverlaidModule"));

        var options = HostingModuleOptions.GetModuleOptions<OverlaidOptions>(brain.GetModuleConfiguration("OverlaidModule"), "OverlaidModule");
        Assert.Equal("from-args", options.Endpoint);
        Assert.Equal(42, options.Delay);
        Assert.Equal("added", options.Bindings["extra"]);
    }
}
