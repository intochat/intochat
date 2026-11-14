using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Kernel.Sandboxing;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public sealed class DisposableBatchSandboxFacts
{
    [Fact]
    public void DockerRunArgumentsEnforceRootlessHardeningReadOnlyRootfsQuotasAndSecretScrubbing()
    {
        var fakeProfile = @"C:\Users\sandboxtester";
        var fakeHome = @"C:\Users\sandboxtester\.intochat";
        var roInput = @"D:\work\jobs\job-01\input";
        var scratchOut = @"D:\work\jobs\job-01\output";

        var sandbox = new DisposableBatchSandbox();
        var spec = new DisposableBatchSandboxSpec(
            SandboxId: "batch-job-01",
            ImageOrPackage: "mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled",
            Command: "/usr/local/bin/worker",
            Arguments: ["--input", "/in/data.csv", "--output", "/out/report.json"],
            ReadOnlyInputHostPath: roInput,
            ScratchOutputHostPath: scratchOut,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BATCH_MODE"] = "strict",
                ["OPENAI_API_KEY"] = "sk-must-not-leak",
                ["GITHUB_TOKEN"] = "ghp-must-not-leak",
                ["DB_PASSWORD"] = "secret-pass",
            },
            MemoryMb: 2048,
            CpuCount: 2,
            PidsLimit: 64,
            AllowPublicNetwork: false);

        var args = sandbox.BuildDockerArguments(spec, fakeProfile, fakeHome);
        var joined = string.Join(' ', args);

        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        AssertPair(args, "--network", "none");
        Assert.Contains("--read-only", args);
        AssertPair(args, "--pids-limit", "64");
        AssertPair(args, "--memory", "2g");
        AssertPair(args, "--cpus", "2");
        AssertPair(args, "--cap-drop", "ALL");
        AssertPair(args, "--security-opt", "no-new-privileges");
        AssertPair(args, "--user", ContainerBatchSandboxRunner.NonRootUserSpec);
        AssertPair(args, "-v", $"{roInput}:/in:ro");
        AssertPair(args, "-v", $"{scratchOut}:/out:rw");
        AssertPair(args, "-e", "BATCH_MODE=strict");

        Assert.DoesNotContain("OPENAI_API_KEY", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-must-not-leak", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_TOKEN", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("DB_PASSWORD", joined, StringComparison.Ordinal);

        // Verify `--` separates container flags from image + command + arguments.
        var separatorIdx = args.ToList().IndexOf("--");
        Assert.True(separatorIdx > 0);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled", args[separatorIdx + 1]);
        Assert.Equal("/usr/local/bin/worker", args[separatorIdx + 2]);
    }

    [Fact]
    public void WindowsSandboxWsbAndWinAppCliManifestsAreDeterministicAndEnforceIsolation()
    {
        var fakeProfile = @"C:\Users\sandboxtester";
        var fakeHome = @"C:\Users\sandboxtester\.intochat";
        var roInput = @"D:\work\wsb\in";
        var scratchOut = @"D:\work\wsb\out";

        var spec = new DisposableBatchSandboxSpec(
            SandboxId: "wsb-render-42",
            ImageOrPackage: "IntoChat.BatchTool",
            Command: @"C:\in\batch-tool.exe",
            Arguments: ["--headless", @"C:\out\result.json"],
            ReadOnlyInputHostPath: roInput,
            ScratchOutputHostPath: scratchOut,
            MemoryMb: 2048,
            CpuCount: 2,
            PidsLimit: 64,
            AllowPublicNetwork: false);

        var wsb1 = WindowsSandboxWsbGenerator.BuildWsbXml(spec, fakeProfile, fakeHome);
        var wsb2 = WindowsSandboxWsbGenerator.BuildWsbXml(spec, fakeProfile, fakeHome);
        Assert.Equal(wsb1, wsb2);

        var doc = XDocument.Parse(wsb1);
        var root = doc.Root!;
        Assert.Equal("Configuration", root.Name.LocalName);
        Assert.Equal("Disable", root.Element("Networking")?.Value);
        Assert.Equal("Disable", root.Element("VGpu")?.Value);
        Assert.Equal("2048", root.Element("MemoryInMB")?.Value);

        var mappedFolders = root.Element("MappedFolders")!.Elements("MappedFolder").ToArray();
        Assert.Equal(2, mappedFolders.Length);

        Assert.Equal(roInput, mappedFolders[0].Element("HostFolder")?.Value);
        Assert.Equal(@"C:\in", mappedFolders[0].Element("SandboxFolder")?.Value);
        Assert.Equal("true", mappedFolders[0].Element("ReadOnly")?.Value);

        Assert.Equal(scratchOut, mappedFolders[1].Element("HostFolder")?.Value);
        Assert.Equal(@"C:\out", mappedFolders[1].Element("SandboxFolder")?.Value);
        Assert.Equal("false", mappedFolders[1].Element("ReadOnly")?.Value);

        // WinApp CLI v0.7.0 sparse-package AppContainer manifest verification.
        var winApp = WindowsSandboxWsbGenerator.BuildWinAppPackageConfig(spec, fakeProfile, fakeHome);
        Assert.Equal("0.7.0", winApp.CliVersion);
        Assert.Contains("--cli-version", winApp.CliArguments);
        Assert.Contains("0.7.0", winApp.CliArguments);
        Assert.Contains("--appcontainer", winApp.CliArguments);
        Assert.Contains("--no-window", winApp.CliArguments);

        using var configDoc = JsonDocument.Parse(winApp.WinAppConfigJson);
        Assert.Equal("0.7.0", configDoc.RootElement.GetProperty("winappCliVersion").GetString());
        Assert.Equal("appContainer", configDoc.RootElement.GetProperty("trustLevel").GetString());
        Assert.False(configDoc.RootElement.GetProperty("allowFullTrust").GetBoolean());
        Assert.Equal("none", configDoc.RootElement.GetProperty("network").GetString());
        Assert.DoesNotContain("runFullTrust", winApp.SparseAppxManifestXml, StringComparison.OrdinalIgnoreCase);

        // Headless SPIKE_WINDOWS=0 guard refuses launching visible WindowsSandbox.exe VMs.
        Assert.Throws<InvalidOperationException>(() =>
            WindowsSandboxWsbGenerator.EnsureVisibleSandboxVmAllowed(static key => key == "SPIKE_WINDOWS" ? "0" : null));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\.ssh")]
    [InlineData(@"C:\Users\alice\.ssh\keys")]
    [InlineData(@"C:\Users\alice\.intochat\vault")]
    [InlineData(@"C:\Users\alice\.intochat\secrets")]
    [InlineData(@"C:\Users\alice\.intochat")]
    [InlineData(@"C:\Users\alice")]
    [InlineData(@"C:\Users\alice\.aws\credentials")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files\Docker")]
    [InlineData(@"C:\")]
    [InlineData(@"/home/alice/.ssh")]
    [InlineData(@"/home/alice/.intochat/vault")]
    [InlineData(@"/etc/passwd")]
    [InlineData(@"/root")]
    [InlineData(@"/var/run/docker.sock")]
    [InlineData(@"D:\work\..\Users\alice\.ssh")]
    [InlineData(@"D:\work\inject:/host:rw")]
    [InlineData(@"\\server\share\folder")]
    public void PathGuardRejectsSensitiveCredentialVaultSystemAndInjectionPaths(string forbiddenPath)
    {
        var fakeProfile = @"C:\Users\alice";
        var fakeHome = @"C:\Users\alice\.intochat";
        var safeScratch = @"D:\work\safe-scratch";

        Assert.ThrowsAny<Exception>(() =>
        {
            var spec = new DisposableBatchSandboxSpec(
                SandboxId: "hostile-path-check",
                ImageOrPackage: "alpine:3.20",
                Command: "/bin/true",
                Arguments: [],
                ReadOnlyInputHostPath: forbiddenPath,
                ScratchOutputHostPath: safeScratch);
            _ = ContainerBatchSandboxRunner.BuildDockerRunArguments(spec, fakeProfile, fakeHome);
        });
    }

    [Fact]
    public void PathGuardRejectsOverlappingReadOnlyInputAndWritableScratchDirectories()
    {
        var fakeProfile = @"C:\Users\alice";
        var fakeHome = @"C:\Users\alice\.intochat";

        var spec = new DisposableBatchSandboxSpec(
            SandboxId: "overlap-check",
            ImageOrPackage: "alpine:3.20",
            Command: "/bin/true",
            Arguments: [],
            ReadOnlyInputHostPath: @"D:\work\batch-run",
            ScratchOutputHostPath: @"D:\work\batch-run\nested-out");

        Assert.Throws<UnauthorizedAccessException>(() =>
            ContainerBatchSandboxRunner.BuildDockerRunArguments(spec, fakeProfile, fakeHome));
    }

    [Fact]
    public async Task ContainerBatchSandboxRunnerEnforcesTimeoutAndTerminatesRunawayProcessWithoutWindows()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempRoot = Directory.CreateTempSubdirectory("intochat-batch-sbx-");
        try
        {
            var roDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "in")).FullName;
            var outDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "out")).FullName;

            ConfinementSpec? capturedConfinement = null;
            var fakeRunner = new FakeConfinedRunner(async (cSpec, token) =>
            {
                capturedConfinement = cSpec;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(cSpec.EffectiveTimeout);
                var sw = Stopwatch.StartNew();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                    return new ConfinedProcessResult(0, "ok", "", TimedOut: false, sw.Elapsed, ConfinementMode.JobObject);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    sw.Stop();
                    return new ConfinedProcessResult(-1, "", "timed out", TimedOut: true, sw.Elapsed, ConfinementMode.JobObject);
                }
            });

            var runner = new ContainerBatchSandboxRunner(
                processRunner: fakeRunner,
                dockerExecutablePath: "docker-mock");

            var spec = new DisposableBatchSandboxSpec(
                SandboxId: "timeout-job-99",
                ImageOrPackage: "alpine:3.20",
                Command: "/bin/sleep",
                Arguments: ["60"],
                ReadOnlyInputHostPath: roDir,
                ScratchOutputHostPath: outDir,
                Timeout: TimeSpan.FromMilliseconds(250));

            var result = await runner.RunAsync(spec, ct);

            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(DisposableSandboxBackend.RootlessDocker, result.Backend);
            Assert.NotNull(capturedConfinement);
            Assert.Equal(NetworkPolicy.None, capturedConfinement!.Network);
            Assert.Contains("--read-only", capturedConfinement.Arguments);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private static void AssertPair(IReadOnlyList<string> args, string flag, string expectedValue)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag && args[i + 1] == expectedValue)
            {
                return;
            }
        }

        Assert.Fail($"Expected consecutive arguments '{flag} {expectedValue}' in [{string.Join(", ", args)}].");
    }

    private sealed class FakeConfinedRunner(Func<ConfinementSpec, CancellationToken, Task<ConfinedProcessResult>> handler) : IConfinedProcessRunner
    {
        public Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
            => handler(spec, cancellationToken);
    }
}
