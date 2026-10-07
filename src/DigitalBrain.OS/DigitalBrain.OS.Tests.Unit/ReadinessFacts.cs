using DigitalBrain.Apps;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DigitalBrain.OS.Tests.Unit;

// The OS is ready when its default apps are published; their behaviors start after readiness, as each
// publisher brain's runtime work. Nothing listens where the sandbox should be, and readiness does not care.
public sealed class ReadinessFacts
{
    private const string NoSandbox = "http://127.0.0.1:9/";

    [Fact(Timeout = 120_000)]
    public async Task ReadinessDoesNotDependOnTheSandbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddOperatingSystemApps();
                silo.Services.PostConfigure<CSharpDeploymentSettings>(options => { options.SandboxUrl = NoSandbox; options.EdgeUrl = NoSandbox; });
            })
            .StartAsync(ct);
        // The OS seeds its own publisher account at startup.
        var os = await brain.SiloServices.GetRequiredService<IPublisherDirectory>().Resolve("os", ct);

        foreach (var publisher in brain.SiloServices.GetServices<IShippedContentPublisher>()) { await publisher.PublishAsync(os, ct); }

        var health = await brain.SiloServices.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Tags.Contains("ready"), ct);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        foreach (var name in new[] { "settings", "assistant", "datavisualizer" })
        {
            var app = await brain.Get<IApp>(IApp.Key(os.ScopeId, name)).Read();
            Assert.NotEmpty(app.Manifest.Title);
            Assert.NotNull(app.PublishedVersion);
        }
        // The runtime tried: its session is wanted and starting, and its reminder will retry the absent host.
        Assert.Equal(SessionStatus.Starting, (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(os.ScopeId)).Inspect(ct)).Session);
    }
}
