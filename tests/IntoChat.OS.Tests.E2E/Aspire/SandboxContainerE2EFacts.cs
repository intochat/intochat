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
        // The command ran as one run of the app's synapse.
        var run = await commandResponse.Content.ReadFromJsonAsync<IntoChat.OS.Modules.Apps.AppRuns.AppRunView>(Ct);
        Assert.NotNull(run);
        Assert.Equal(IntoChat.OS.Modules.Apps.AppRuns.AppRunStatus.Completed, run!.Status);
        Assert.Equal("email-notifier", run.App);
        Assert.StartsWith("commands/email-notifier/", run.RunId, StringComparison.Ordinal);

        var notifications = await host.Os.GetFromJsonAsync<NotificationInboxPayload>(
            $"/brains/{host.Scope}/notifications",
            Ct);
        Assert.NotNull(notifications);
        var alert = Assert.Single(
            notifications!.Items,
            item => string.Equals(item.Title, "New email: Q4 Invoice Approved", StringComparison.Ordinal));
        Assert.Equal("email-notifier", alert.SourceApp);
    }

    // An installed app's [Every] schedule ticks by itself: each due tick is a run of the app, and this one raises a notification.
    [Fact]
    public async Task AScheduledSandboxAppTicksAsRunsOfTheApp()
    {
        await using var host = new OsAppHostFixture(
            [$"--{ModuleConfiguration.ModuleSection(SandboxModuleHosting.ModuleName)}:Enabled=true"],
            TimeSpan.FromSeconds(300));
        await host.InitializeAsync();
        await host.App.ResourceNotifications.WaitForResourceHealthyAsync(SandboxModuleHosting.ResourceName, Ct);

        const string code = """
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            namespace IntoChat.Tests;

            internal sealed record TickState { public int Ticks { get; init; } }
            internal sealed record TickSettings;

            [App("ticker", Title = "Ticker", Summary = "Ticks every minute.", Version = "1.0.0")]
            internal sealed class TickerApp : BrainApp<TickState, TickSettings>
            {
                [Every("1m")]
                public async Task Tick()
                {
                    State = State with { Ticks = State.Ticks + 1 };
                    await Brain.Notifications.SendAsync(new NotificationRequest(Title: $"Tick {State.Ticks}", Body: "ticked", Category: "test"));
                }

                [OnCommand("ping", Description = "Answers pong.")]
                public Task<string> Ping() => Task.FromResult("pong");
            }
            """;
        AppSourceFile[] files = [new("TickerApp.cs", code)];
        var package = new AppPackage("ticker", "1.0.0", "Ticker", "Ticks every minute.", "clock", "Test", "os", files,
            new AppPackageNeeds([], [], ["1m"], [new AppCommandSpec("ping", "Answers pong.")]), "{}", AppPackage.ComputeDigest("ticker", "1.0.0", files));
        using var importResponse = await host.Os.PostAsJsonAsync($"/brains/{host.Scope}/apps/import", package.ToAppBundle(), Ct);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        using var ping = await host.Os.PostAsync($"/brains/{host.Scope}/apps/ticker/command/ping", content: null, Ct);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);

        // Reminders tick no faster than once a minute; allow a few.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        NotificationItem? tick = null;
        while (tick is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), Ct);
            var inbox = await host.Os.GetFromJsonAsync<NotificationInboxPayload>($"/brains/{host.Scope}/notifications", Ct);
            tick = inbox?.Items.FirstOrDefault(item => item.Title.StartsWith("Tick ", StringComparison.Ordinal) && item.SourceApp == "ticker");
        }
        Assert.NotNull(tick);
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
