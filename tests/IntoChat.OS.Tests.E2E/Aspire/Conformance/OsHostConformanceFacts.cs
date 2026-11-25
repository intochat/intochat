using System.Net;
using System.Text.Json;
using DigitalBrain.Testing.Conformance.Host;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Conformance;

[Collection(OsAppHostCollection.Name)]
public sealed class OsHostConformanceFacts(OsAppHostFixture host)
{
    [Fact]
    public void TheTargetHostsEveryOsModule()
    {
        var expected = IntoChatOsTarget.ExpectedOsModules()
            .Append(typeof(FrameworkModule))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(IntoChatOsTarget.ExpectedOsModules());
        Assert.Equal(expected, IntoChatOsTarget.Modules);
    }

    [Fact]
    public async Task TheAppHostWiresHealthAppsAndScreenHttpLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);

        using var health = await host.Os.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var apps = await host.Os.GetAsync($"/brains/{host.Scope}/apps", ct);
        Assert.Equal(HttpStatusCode.OK, apps.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await apps.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var installed = doc.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var root in ShippedApps.Roots.Where(r => !string.Equals(r, "circleroom", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(root, installed);
        }

        await using var visual = VisualDebugWindow.LaunchIfEnabled(host);
        await using var driver = new AspireHttpSessionDriver(host, "settings");

        var initialView = await driver.OpenAsync(ct: ct);
        Assert.Equal(JsonValueKind.Object, initialView.View.ValueKind);
        Assert.True(initialView.Generation >= 0);

        var activatedView = await driver.ClickAsync("nav-desktop", ct);
        Assert.Equal(JsonValueKind.Object, activatedView.View.ValueKind);
        Assert.True(activatedView.Generation >= initialView.Generation);
    }
}
