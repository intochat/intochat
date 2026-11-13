using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

[Collection(OsAppHostCollection.Name)]
public sealed class BootFacts(OsAppHostFixture host)
{
    [Fact]
    public async Task TheOperatingSystemBootsAsAnApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);
        Assert.True(host.StartupDuration < TimeSpan.FromSeconds(120), $"Fixture startup took {host.StartupDuration.TotalSeconds:F1}s, exceeding 120s.");

        using var health = await host.Os.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var apps = await host.Os.GetAsync($"/brains/{host.Scope}/apps", ct);
        Assert.Equal(HttpStatusCode.OK, apps.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await apps.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var installed = doc.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

        // CircleRoom is only installed in circle brains (ShippedInstall.Missing skips circleroom for personal brains).
        foreach (var root in ShippedApps.Roots.Where(r => !string.Equals(r, "circleroom", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(root, installed);
        }
    }

    [Fact]
    public void TheHostComposesTheSameResourcesAsHeadless()
    {
        var model = host.App.Services.GetRequiredService<DistributedApplicationModel>();
        var names = model.Resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OperatingSystem-a", names);
        Assert.Empty(model.Resources.OfType<ContainerResource>());
        Assert.Empty(model.Resources.OfType<YarpResource>());
        Assert.DoesNotContain(model.Resources, r => r.Annotations.Any(a => a.GetType().Name.EndsWith("BuildAnnotation", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedStartLeavesNoProcessBehind()
    {
        var bad = new OsAppHostFixture([$"--{IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyKey}=nowhere"]);
        var tempHome = bad.Home;

        await Assert.ThrowsAnyAsync<Exception>(async () => await bad.InitializeAsync());

        Assert.False(Directory.Exists(tempHome), $"Temporary home {tempHome} still exists after failed start.");
        var lingering = Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    return !p.HasExited && p.MainModule?.FileName?.Contains(tempHome, StringComparison.OrdinalIgnoreCase) == true;
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
        Assert.Empty(lingering);
    }
}
