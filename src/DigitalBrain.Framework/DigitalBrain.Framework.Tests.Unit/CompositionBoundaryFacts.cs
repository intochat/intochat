using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Azure;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Metering;
using DigitalBrain.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class CompositionBoundaryFacts
{
    private sealed class SampleModule() : Module(new ModuleId("sample"))
    { public override void Configure(ISiloBuilder silo) { } }

    private sealed class BoundaryModule() : Module(new ModuleId("module"))
    { public override void Configure(ISiloBuilder silo) { } }

    [Fact]
    public void TwoBrainsHaveDistinctResourcesAndModuleNodes()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var first = builder.AddDigitalBrain("first", options: new() { PersistentStorage = false }).WithModule(new SampleModule());
        var second = builder.AddDigitalBrain("second", options: new() { PersistentStorage = false }).WithModule(new SampleModule());
        Assert.NotSame(first.GetOrAddModuleNode("sample").Resource, second.GetOrAddModuleNode("sample").Resource);
        var names = builder.Resources.Select(r => r.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("first-sample", names);
        Assert.Contains("second-sample", names);
    }

    [Fact]
    public void UnknownSelectionIsRejectedBeforeAnyModuleConfigures()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[DigitalBrainConfiguration.ModuleSection("a") + ":Enabled"] = "true";
        builder.Configuration[DigitalBrainConfiguration.ModuleSection("z") + ":Enabled"] = "true";
        var module = new AModule();
        Assert.Throws<InvalidOperationException>(() => builder.AddDigitalBrainServer(server => server.AddModule(module)));
        Assert.Equal(0, module.SiloConfigurations);
    }

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var server = new DigitalBrainServerBuilder().AddModule<StatefulModule>();
        Assert.Throws<InvalidOperationException>(server.AddModule<StatefulModule>);
    }

    [Fact]
    public async Task SiloAndHttpConfigurationUseTheSameInstanceAndUnselectedModulesDoNotRun()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[DigitalBrainConfiguration.ModuleSection("unused") + ":Enabled"] = "false";
        var instance = new StatefulModule();
        var unused = new UnusedModule();
        builder.AddDigitalBrainServer(server => server.AddModule(instance).AddModule(unused));
        await using var app = builder.Build();
        app.MapDigitalBrainModules();
        Assert.Equal(1, instance.SiloConfigurations);
        Assert.Equal(1, instance.HttpConfigurations);
        Assert.Equal(0, unused.SiloConfigurations);
        Assert.Same(instance, Assert.Single(app.Services.GetServices<Module>()));
    }

    [Fact]
    public void DefaultAppHostDoesNotProvisionAzureAndRejectsDuplicatesBeforeAReferenceExists()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("brain").WithModule(new BoundaryModule());
        Assert.Empty(builder.Resources.OfType<AzureStorageResource>());
        Assert.Throws<InvalidOperationException>(() => brain.WithModule(new BoundaryModule()));
        Assert.Throws<InvalidOperationException>(() => brain.AddBlobContainer("requires-azure"));
    }

    [Fact]
    public void V1WirePathsAndJsonRemainStable()
    {
        Assert.Equal("runtime/v1/invoke", RuntimeProtocol.Invoke);
        Assert.Equal("runtime/v1/report", RuntimeProtocol.Report);
        Assert.Equal("runtime/v1/stream", RuntimeProtocol.Stream);
        Assert.Equal("X-DigitalBrain-Refusal", RuntimeProtocol.RefusalHeader);
        var invocation = new RuntimeInvocation("behavior", "contract", "key", "method", [JsonSerializer.SerializeToElement(42)]);
        Assert.Equal("{\"behavior\":\"behavior\",\"contract\":\"contract\",\"key\":\"key\",\"method\":\"method\",\"arguments\":[42]}", JsonSerializer.Serialize(invocation, RuntimeProtocol.Json));
    }

    [Fact]
    public async Task ConcurrentUsageIsCollectedAndSnapshotsAreDetached()
    {
        using var context = IntentContext.Begin("intent");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            Assert.Same(context, IntentContext.Current);
            context.AddUsage(new Entry());
        }, TestContext.Current.CancellationToken)));
        var snapshot = context.Snapshot();
        context.AddUsage(new Entry());
        Assert.Equal(100, snapshot.Usage.Length);
        Assert.Equal(101, context.Usage.Length);
    }

    [Fact]
    public void IntentScopesEnforceNestingAndRestoreTheirParent()
    {
        using var outer = IntentContext.Begin("outer");
        var inner = IntentContext.Begin("inner");
        Assert.Throws<InvalidOperationException>(outer.Dispose);
        inner.Dispose();
        Assert.Same(outer, IntentContext.Current);
        inner.Dispose();
    }

    private sealed record Entry : IIntentUsageEntry;
    private sealed class AModule() : Module(new ModuleId("a"))
    {
        public int SiloConfigurations { get; private set; }
        public override void Configure(ISiloBuilder silo) => SiloConfigurations++;
    }
    private sealed class UnusedModule() : Module(new ModuleId("unused"))
    {
        public int SiloConfigurations { get; private set; }
        public override void Configure(ISiloBuilder silo) => SiloConfigurations++;
    }
    public sealed class StatefulModule() : Module(new ModuleId("stateful")), IHttpModule
    {
        public int SiloConfigurations { get; private set; }
        public int HttpConfigurations { get; private set; }
        public override void Configure(ISiloBuilder silo) => SiloConfigurations++;
        public void Configure(IEndpointRouteBuilder endpoints) => HttpConfigurations++;
    }
}
