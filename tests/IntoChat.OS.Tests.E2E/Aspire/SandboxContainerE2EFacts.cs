using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.Sdk;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

[Trait("tier", "nightly")]
[Collection(OsAppHostCollection.Name)]
public sealed class SandboxContainerE2EFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HardenedSandboxContainerExecutesEmailNotifierAndEmitsOsNotification()
    {
        await using var host = new OsAppHostFixture(
            [$"--{ModuleConfiguration.ModuleSection(SandboxModuleHosting.ModuleName)}:Enabled=true"],
            TimeSpan.FromSeconds(300));
        await host.InitializeAsync();
        await host.App.ResourceNotifications.WaitForResourceHealthyAsync(SandboxModuleHosting.ResourceName, Ct);

        var containerId = await FindRunningSandboxContainerIdAsync(Ct);
        Assert.False(string.IsNullOrWhiteSpace(containerId), "Expected running sandbox container in Docker.");

        var inspectJson = await RunDockerAsync(["inspect", containerId!], Ct);
        using var inspectDoc = JsonDocument.Parse(inspectJson);
        var container = inspectDoc.RootElement.EnumerateArray().First();
        Assert.True(container.GetProperty("State").GetProperty("Running").GetBoolean(), "Sandbox container must be running.");
        Assert.True(container.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean(), "Sandbox container must run with --read-only rootfs.");

        var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
        Assert.DoesNotContain(
            mounts,
            mount => string.Equals(mount.GetProperty("Type").GetString(), "bind", StringComparison.OrdinalIgnoreCase));

        var package = ShippedPackages.Find("email-notifier")!;
        using var importResponse = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/import",
            package.ToAppBundle(),
            Ct);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        using var commandResponse = await host.Os.PostAsync(
            $"/brains/{host.Scope}/apps/email-notifier/command/test-alert",
            content: null,
            Ct);
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);

        var notifications = await host.Os.GetFromJsonAsync<NotificationInboxPayload>(
            $"/brains/{host.Scope}/notifications",
            Ct);
        Assert.NotNull(notifications);
        var alert = Assert.Single(
            notifications!.Items,
            item => string.Equals(item.Title, "New email: Q4 Invoice Approved", StringComparison.Ordinal));
        Assert.Equal("email-notifier", alert.SourceApp);
    }

    private sealed record NotificationInboxPayload(NotificationItem[] Items, int UnreadCount);

    private static async Task<string?> FindRunningSandboxContainerIdAsync(CancellationToken ct)
    {
        var output = await RunDockerAsync(
            ["ps", "--filter", "status=running", "--filter", $"name={SandboxModuleHosting.ResourceName}", "--format", "{{.ID}}"],
            ct);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private static async Task<string> RunDockerAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }
}
