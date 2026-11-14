using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Desktop.Tests.Unit;

public sealed class DesktopLifecycleFacts
{
    [Fact]
    public void AtomicKernelPidRecordWritesToKernelPidPathAndRoundTripsMetadata()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-pid-atomic-");
        try
        {
            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            Assert.EndsWith(Path.Combine("run", "kernel.pid"), layout.KernelPidFilePath, StringComparison.OrdinalIgnoreCase);

            DesktopBootstrap.WriteKernelPidRecord(
                layout.KernelPidFilePath,
                pid: 42420,
                port: 5080,
                unitHostPid: 42421,
                background: true);

            Assert.True(File.Exists(layout.KernelPidFilePath));
            Assert.Empty(Directory.GetFiles(layout.RunDirectory, "*.tmp.*"));

            Assert.True(DesktopBootstrap.TryReadLayoutPidRecord(layout, out var record));
            Assert.Equal(42420, record.Pid);
            Assert.Equal(5080, record.Port);
            Assert.Equal(42421, record.UnitHostPid);
            Assert.True(record.Background);
            Assert.False(string.IsNullOrWhiteSpace(record.StartedAt));

            // Plain integer PID files written by external supervisors are also accepted.
            var plainPidPath = Path.Combine(layout.RunDirectory, "plain.pid");
            File.WriteAllText(plainPidPath, "19840\n", Encoding.UTF8);
            Assert.True(DesktopBootstrap.TryReadKernelPidRecord(plainPidPath, out var plainPid, out var plainPort));
            Assert.Equal(19840, plainPid);
            Assert.Equal(0, plainPort);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StatusProbeFormatsJsonAndTextInUnderFiveMilliseconds()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-status-probe-");
        try
        {
            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            _ = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: true);
            DesktopBootstrap.WriteKernelPidRecord(
                layout.KernelPidFilePath,
                pid: Environment.ProcessId,
                port: 5088,
                unitHostPid: Environment.ProcessId,
                background: true);

            // Warm up once before measuring steady-state sub-5ms status probe.
            _ = DesktopBootstrap.ProbeStatus(layout, 5080, isPortListening: static p => p == 5088);

            var sw = Stopwatch.StartNew();
            var running = DesktopBootstrap.ProbeStatus(layout, 5080, isPortListening: static p => p == 5088);
            var jsonText = DesktopBootstrap.FormatStatus(running, textFormat: false);
            var humanText = DesktopBootstrap.FormatStatus(running, textFormat: true);
            sw.Stop();

            Assert.True(
                sw.Elapsed.TotalMilliseconds < 5.0,
                $"Status probe + formatting took {sw.Elapsed.TotalMilliseconds:F3}ms (expected < 5ms).");
            Assert.Equal("running", running.Status);
            Assert.True(running.Healthy);
            Assert.Equal(Environment.ProcessId, running.Pid);
            Assert.Equal(Environment.ProcessId, running.UnitHostPid);
            Assert.Equal(5088, running.Port);
            Assert.True(running.Background);
            Assert.False(running.StalePidFile);

            using var parsed = JsonDocument.Parse(jsonText);
            Assert.Equal("running", parsed.RootElement.GetProperty("status").GetString());
            Assert.True(parsed.RootElement.GetProperty("healthy").GetBoolean());
            Assert.Equal(Environment.ProcessId, parsed.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(5088, parsed.RootElement.GetProperty("port").GetInt32());
            Assert.True(parsed.RootElement.GetProperty("background").GetBoolean());
            Assert.Contains("IntoChat OS: running", humanText, StringComparison.Ordinal);

            // Dead PID + closed socket marks stalePidFile = true and status = stopped.
            var stale = DesktopBootstrap.ProbeStatus(
                layout,
                5080,
                isPortListening: static _ => false,
                isProcessAlive: static _ => false);
            Assert.Equal("stopped", stale.Status);
            Assert.False(stale.Healthy);
            Assert.Null(stale.Pid);
            Assert.True(stale.StalePidFile);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void BackgroundAndHideOnCloseModesKeepKernelAliveUnlessNoTrayOrHeadlessSpikeIsSet()
    {
        Assert.True(DesktopBootstrap.IsStatusCommand(["--status"]));
        Assert.True(DesktopBootstrap.IsStatusCommand(["status"]));
        Assert.True(DesktopBootstrap.IsQuitCommand(["--quit"]));
        Assert.True(DesktopBootstrap.IsQuitCommand(["--stop"]));
        Assert.True(DesktopBootstrap.IsQuitCommand(["stop"]));
        Assert.True(DesktopBootstrap.IsBackgroundDaemon(["--background"]));
        Assert.True(DesktopBootstrap.IsBackgroundDaemon(["--daemon"]));

        // Default desktop interactive mode keeps kernel alive on window close (tray/daemon posture).
        Assert.True(DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose([], _ => null));

        // Explicit --background / --hide-on-close keeps kernel alive even when SPIKE_WINDOWS=0.
        Assert.True(DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose(
            ["--background"],
            key => key == DesktopBootstrap.SpikeWindowsEnvVar ? "0" : null));
        Assert.True(DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose(
            ["--hide-on-close"],
            key => key == DesktopBootstrap.SpikeWindowsEnvVar ? "0" : null));

        // --no-tray and ephemeral SPIKE_WINDOWS=0 without --background terminate owned kernel on exit.
        Assert.False(DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose(["--no-tray"], _ => null));
        Assert.False(DesktopBootstrap.ShouldKeepKernelAliveOnWindowClose(
            [],
            key => key == DesktopBootstrap.SpikeWindowsEnvVar ? "0" : null));
    }

    [Fact]
    public async Task QuitAuthenticatesWithPresenceTokenGracefullyStopsAndTerminatesResidualKernelAndUnitHostPids()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("intochat-quit-lifecycle-");
        try
        {
            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            var presence = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: true);
            DesktopBootstrap.WriteKernelPidRecord(
                layout.KernelPidFilePath,
                pid: 71001,
                port: 5091,
                unitHostPid: 71002,
                background: true);
            File.WriteAllText(layout.LegacyKernelPidFilePath!, "{\"pid\":71001,\"port\":5091}");

            var alivePids = new HashSet<int> { 71001, 71002 };
            var killedPids = new List<int>();
            string? capturedPresenceHeader = null;
            string? capturedBearerToken = null;
            Uri? capturedUri = null;

            using var handler = new DelegateHttpMessageHandler(request =>
            {
                capturedUri = request.RequestUri;
                capturedPresenceHeader = request.Headers.TryGetValues(DesktopBootstrap.PresenceHeaderName, out var vals)
                    ? vals.FirstOrDefault()
                    : null;
                capturedBearerToken = request.Headers.Authorization?.Parameter;

                // Simulate kernel draining cleanly right as /runtime/shutdown succeeds, while UnitHost lingers until forced kill.
                alivePids.Remove(71001);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"stopping\":true}", Encoding.UTF8, "application/json"),
                };
            });

            var stopResult = await DesktopBootstrap.StopRunningKernelAsync(
                layout,
                configuredPort: 5080,
                httpHandler: handler,
                killProcessTree: pid =>
                {
                    killedPids.Add(pid);
                    alivePids.Remove(pid);
                },
                isProcessAlive: alivePids.Contains,
                isPortListening: static port => port == 5091,
                cancellationToken: ct);

            Assert.True(stopResult.GracefulShutdownAccepted);
            Assert.False(stopResult.TerminatedKernelPid); // Exited gracefully during drain!
            Assert.True(stopResult.TerminatedUnitHostPid); // Lingering UnitHost terminated cleanly!
            Assert.True(stopResult.CleanedRunFiles);

            Assert.Equal("http://127.0.0.1:5091/runtime/shutdown", capturedUri?.ToString());
            Assert.Equal(presence, capturedPresenceHeader);
            Assert.Equal(presence, capturedBearerToken);
            Assert.Equal([71002], killedPids);

            Assert.False(File.Exists(layout.KernelPidFilePath));
            Assert.False(File.Exists(layout.LegacyKernelPidFilePath!));
            Assert.False(File.Exists(layout.PresenceFilePath));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RuntimeShutdownEndpointsEnforceLoopbackAndConstantTimePresenceTokenVerification()
    {
        var validToken = DesktopBootstrap.GeneratePresenceToken();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RuntimeShutdownEndpoints.PresenceTokenEnvKey] = validToken,
            })
            .Build();

        Assert.Equal(validToken, RuntimeShutdownEndpoints.ResolveExpectedPresenceToken(config));

        var loopbackCtx = new DefaultHttpContext();
        loopbackCtx.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(RuntimeShutdownEndpoints.IsLoopbackRequest(loopbackCtx));

        var remoteCtx = new DefaultHttpContext();
        remoteCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.44");
        Assert.False(RuntimeShutdownEndpoints.IsLoopbackRequest(remoteCtx));

        // Missing token -> rejected.
        Assert.False(RuntimeShutdownEndpoints.HasValidPresenceToken(loopbackCtx.Request, validToken));

        // Wrong token -> rejected.
        loopbackCtx.Request.Headers[RuntimeShutdownEndpoints.PresenceHeaderName] = DesktopBootstrap.GeneratePresenceToken();
        Assert.False(RuntimeShutdownEndpoints.HasValidPresenceToken(loopbackCtx.Request, validToken));

        // Exact X-Presence-Token -> accepted.
        loopbackCtx.Request.Headers[RuntimeShutdownEndpoints.PresenceHeaderName] = validToken;
        Assert.True(RuntimeShutdownEndpoints.HasValidPresenceToken(loopbackCtx.Request, validToken));

        // Authorization: Bearer <token> -> accepted.
        var bearerCtx = new DefaultHttpContext();
        bearerCtx.Request.Headers.Authorization = "Bearer " + validToken;
        Assert.True(RuntimeShutdownEndpoints.HasValidPresenceToken(bearerCtx.Request, validToken));
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
