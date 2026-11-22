using System.Diagnostics;
using System.Net;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests.Unit;

public sealed class ProcessCandidateHostFacts : IDisposable
{
    private readonly string _workdir;

    public ProcessCandidateHostFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-proc-cand-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workdir))
            {
                Directory.Delete(_workdir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Run_UsesIsolatedHomeNotLiveHome_FreePortsNot5080Or5081_AndHeadlessNoWindow()
    {
        ProcessStartInfo? captured = null;
        var portsOffered = new Queue<int>([5080, 5081, 49152]);
        using var http = new HttpClient(new StaticStatusHandler(HttpStatusCode.OK));
        var runner = new StubProcessRunner();
        var host = new ProcessCandidateHost(
            runner,
            _ => Task.FromResult(_workdir),
            secrets: null,
            launcher: psi =>
            {
                captured = psi;
                return CreateFakeProcess(pid: 4242, hasExited: false);
            },
            http: http,
            portAllocator: () => portsOffered.Count > 0 ? portsOffered.Dequeue() : 49153);

        var running = await host.Run("a1b2c3d", Ct);

        Assert.NotNull(captured);
        Assert.Equal("a1b2c3d", running.Sha);
        Assert.Equal("proc-a1b2c3d", running.Container);

        // Ports must never be 5080 or 5081
        var endpointUri = new Uri(running.Endpoints.Ui);
        Assert.Equal("127.0.0.1", endpointUri.Host);
        Assert.NotEqual(5080, endpointUri.Port);
        Assert.NotEqual(5081, endpointUri.Port);
        Assert.Equal(49152, endpointUri.Port);
        Assert.Equal($"http://127.0.0.1:{endpointUri.Port}", captured.Environment["ASPNETCORE_URLS"]);

        // Headless process flags
        Assert.True(captured.CreateNoWindow);
        Assert.False(captured.UseShellExecute);
        Assert.True(captured.RedirectStandardOutput);
        Assert.True(captured.RedirectStandardError);
        Assert.Equal("None", captured.Environment["IntoChatOS__Modules__Flutter__Host"]);
        Assert.Equal("local", captured.Environment["IntoChatOS__Topology"]);

        // Isolated home must never equal or be inside ~/.intochat
        var candidateHome = captured.Environment["IntoChatOS__Home"];
        Assert.False(string.IsNullOrWhiteSpace(candidateHome));
        Assert.True(Directory.Exists(candidateHome));
        var liveHome = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".intochat"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.False(string.Equals(Path.GetFullPath(candidateHome), liveHome, comparison));
        Assert.False(Path.GetFullPath(candidateHome).StartsWith(liveHome + Path.DirectorySeparatorChar, comparison));

        // Cleanup removes the tracked process and deletes the isolated home
        await host.Remove("a1b2c3d", Ct);
        Assert.False(Directory.Exists(candidateHome));

        // Explicitly reject a workdir inside ~/.intochat
        var forbiddenHost = new ProcessCandidateHost(
            runner,
            _ => Task.FromResult(Path.Combine(liveHome, "forbidden-workdir")),
            launcher: _ => CreateFakeProcess(pid: 9999, hasExited: false),
            http: http);
        var ex = await Assert.ThrowsAsync<CandidateHostException>(() => forbiddenHost.Run("b2c3d4e", Ct));
        Assert.Equal("run", ex.Stage);
        Assert.Contains(".intochat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("needs", "process")]
    public async Task Run_AndRemove_RealProcess_ExitsCleanlyAndCleansHome()
    {
        var runner = new StubProcessRunner();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        int spawnedPid = -1;
        string? candidateHome = null;

        var host = new ProcessCandidateHost(
            runner,
            _ => Task.FromResult(_workdir),
            secrets: null,
            launcher: psi =>
            {
                candidateHome = psi.Environment["IntoChatOS__Home"];
                var urls = psi.Environment["ASPNETCORE_URLS"]!;
                var port = new Uri(urls).Port;

                // Spawn a real lightweight headless OS process serving HTTP 200 on /health on the allocated loopback port.
                var realStart = new ProcessStartInfo("powershell")
                {
                    WorkingDirectory = candidateHome!,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                realStart.ArgumentList.Add("-NoProfile");
                realStart.ArgumentList.Add("-NonInteractive");
                realStart.ArgumentList.Add("-Command");
                realStart.ArgumentList.Add(
                    $"$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, {port}); " +
                    "$l.Start(); " +
                    "while ($true) { " +
                    "  $c = $l.AcceptTcpClient(); " +
                    "  $s = $c.GetStream(); " +
                    "  $b = [System.Text.Encoding]::ASCII.GetBytes(\"HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`nOK\"); " +
                    "  $s.Write($b, 0, $b.Length); " +
                    "  $s.Flush(); " +
                    "  $c.Close(); " +
                    "}");
                var proc = CandidateProcessHandle.Start(realStart);
                spawnedPid = proc.Id;
                return proc;
            },
            http: http);

        var sha = "c3d4e5f";
        try
        {
            var running = await host.Run(sha, Ct);
            Assert.True(spawnedPid > 0);
            Assert.NotNull(candidateHome);
            Assert.True(Directory.Exists(candidateHome));
            Assert.NotEqual(5080, new Uri(running.Endpoints.Ui).Port);
            Assert.NotEqual(5081, new Uri(running.Endpoints.Ui).Port);

            await host.WaitHealthy(sha, TimeSpan.FromSeconds(15), Ct);

            using var osProc = Process.GetProcessById(spawnedPid);
            Assert.False(osProc.HasExited);

            await host.Remove(sha, Ct);

            Assert.True(osProc.HasExited);
            Assert.False(Directory.Exists(candidateHome));
            Assert.False(host.TryGetTracked(sha, out _));
        }
        finally
        {
            try { await host.Remove(sha, CancellationToken.None); } catch { }
        }
    }

    [Fact]
    public async Task WaitHealthy_FailsFastWhenCandidateProcessCrashesOnStartup()
    {
        var crashed = CreateFakeProcess(
            pid: 5555,
            hasExited: true,
            exitCode: 139,
            stderr: "Unhandled exception. System.InvalidOperationException: Boot config missing");
        using var http = new HttpClient(new StaticStatusHandler(HttpStatusCode.ServiceUnavailable));
        var host = new ProcessCandidateHost(
            new StubProcessRunner(),
            _ => Task.FromResult(_workdir),
            launcher: _ => crashed,
            http: http);

        await host.Run("d4e5f6a", Ct);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<CandidateHostException>(
            () => host.WaitHealthy("d4e5f6a", TimeSpan.FromSeconds(30), Ct));
        sw.Stop();

        Assert.Equal("healthy", ex.Stage);
        Assert.Contains("139", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Boot config missing", ex.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected fast fail on crash, took {sw.Elapsed}");

        await host.Remove("d4e5f6a", Ct);
    }

    private static CandidateProcessHandle CreateFakeProcess(
        int pid,
        bool hasExited,
        int exitCode = 0,
        string stdout = "",
        string stderr = "")
    {
        var exited = hasExited;
        return new CandidateProcessHandle(
            pid,
            hasExited: () => exited,
            exitCode: () => exitCode,
            stdout: () => stdout,
            stderr: () => stderr,
            kill: _ => exited = true,
            waitForExit: _ => Task.CompletedTask);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", ""));

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }

    private sealed class StaticStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
