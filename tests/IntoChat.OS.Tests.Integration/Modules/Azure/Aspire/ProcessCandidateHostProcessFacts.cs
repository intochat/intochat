using System.Diagnostics;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Tests.Integration;

// Spawns a real headless process that serves /health on a loopback socket, so it lives in the Integration tier.
public sealed class ProcessCandidateHostProcessFacts : IDisposable
{
    private readonly string _workdir;

    public ProcessCandidateHostProcessFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-proc-cand-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workdir))
        {
            Directory.Delete(_workdir, recursive: true);
        }
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

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string[] args, string workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", ""));

        public int Detach(string file, string[] args, string workingDirectory) => 1;
    }
}
