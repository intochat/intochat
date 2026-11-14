using System.Diagnostics;
using DigitalBrain.UI;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Sandbox;
using IntoChat.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public sealed class ConfinedProcessAndUnitHostFacts
{
    private const string SampleUnitSource = """
        using System.Threading;
        using DigitalBrain.UI;
        using IntoChat.Sdk;

        namespace SampleUnitHostTest;

        public sealed class CounterState
        {
            public int Value { get; set; }
        }

        public sealed class CounterSettings
        {
            public string Label { get; set; } = "Count";
        }

        [App("unit-counter", Title = "Unit Counter", Summary = "Out-of-process UnitHost test app")]
        public sealed class UnitCounterApp : BrainApp<CounterState, CounterSettings>
        {
            [OnCommand("add", Description = "Adds delta")]
            public int Add(int delta = 1)
            {
                State.Value += delta;
                return State.Value;
            }

            [OnCommand("spin-forever", Description = "Infinite CPU loop for watchdog test")]
            public int SpinForever()
            {
                while (true)
                {
                    Thread.SpinWait(10_000);
                }
            }

            public override View Render()
                => new("Counter", new Text($"{Settings.Label}: {State.Value}"));
        }
        """;

    [Fact]
    public void PosixBuildersEnforceCleanEnvironmentGitReadOnlyGuardAndNetworkConfinement()
    {
        var spec = new ConfinementSpec(
            SandboxId: "sbx-posix-1",
            ExecutablePath: "/usr/bin/git",
            Arguments: ["status"],
            WorkingDirectory: "/work/wt1",
            ReadOnlyPaths: ["/opt/sdk"],
            ReadWritePaths: ["/work/wt1"],
            CleanEnvironment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ALLOWED_FLAG"] = "yes",
                ["OPENAI_API_KEY"] = "must-not-pass",
                ["GH_TOKEN"] = "must-not-pass",
            },
            Network: NetworkPolicy.None);

        var bwrapArgs = PosixSandboxCommandBuilder.BuildBubblewrapArguments(spec);
        Assert.Contains("--unshare-net", bwrapArgs);
        Assert.Contains("--clearenv", bwrapArgs);
        Assert.Contains("/work/wt1/.git", bwrapArgs);
        Assert.Contains("ALLOWED_FLAG", bwrapArgs);
        Assert.DoesNotContain("OPENAI_API_KEY", bwrapArgs);
        Assert.DoesNotContain("GH_TOKEN", bwrapArgs);

        var seatbelt = PosixSandboxCommandBuilder.BuildSeatbeltProfile(spec, "/tmp/sbx");
        Assert.Contains("(deny default)", seatbelt, StringComparison.Ordinal);
        Assert.Contains("(deny network*)", seatbelt, StringComparison.Ordinal);
        Assert.Contains("(deny file-write* (subpath \"/work/wt1/.git\"))", seatbelt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfinedProcessRunnerStripsHostSecretsAndExecutesCleanlyWithZeroWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("intochat-confined-env-");
        var secretVarName = "UNIT_TEST_MASTER_SECRET";
        Environment.SetEnvironmentVariable(secretVarName, "super-secret-do-not-inherit");

        try
        {
            var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var runner = new WindowsAppContainerRunner();
            var spec = new ConfinementSpec(
                SandboxId: "sbx-clean-env",
                ExecutablePath: cmdExe,
                Arguments: ["/d", "/c", $"echo ALLOWED=%SAFE_GREETING% SECRET=%{secretVarName}%"],
                WorkingDirectory: tempDir.FullName,
                ReadOnlyPaths: [],
                ReadWritePaths: [tempDir.FullName],
                CleanEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SAFE_GREETING"] = "hello-sandbox",
                    ["LEAKED_API_KEY"] = "blocked-value",
                },
                Timeout: TimeSpan.FromSeconds(10));

            var result = await runner.RunAsync(spec, ct);

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ALLOWED=hello-sandbox", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-do-not-inherit", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("blocked-value", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVarName, null);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConfinedProcessRunnerJobObjectKillsInfiniteLoopChildAtTimeoutWithZeroLingeringProcesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("intochat-confined-timeout-");
        try
        {
            var powershellExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            var pidFile = Path.Combine(tempDir.FullName, "child.pid");
            var script = $"[System.IO.File]::WriteAllText('{pidFile.Replace("'", "''", StringComparison.Ordinal)}', $PID.ToString()); while ($true) {{ Start-Sleep -Milliseconds 50 }}";

            var runner = new WindowsAppContainerRunner();
            var spec = new ConfinementSpec(
                SandboxId: "sbx-infinite-loop",
                ExecutablePath: powershellExe,
                Arguments: ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                WorkingDirectory: tempDir.FullName,
                ReadOnlyPaths: [],
                ReadWritePaths: [tempDir.FullName],
                CleanEnvironment: new Dictionary<string, string>(),
                Timeout: TimeSpan.FromMilliseconds(750));

            var result = await runner.RunAsync(spec, ct);

            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);

            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var childPid))
            {
                AssertProcessExited(childPid);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnitHostRunnerExecutesTurnsWithoutHostSecretsAndKillsInfiniteLoopTurnAtWatchdogTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("intochat-unithost-test-");
        var secretEnvName = "FAKE_HOST_MASTER_KEY";
        Environment.SetEnvironmentVariable(secretEnvName, "top-secret-master-key-value");

        try
        {
            var assemblyBase64 = CompileUnitAssemblyBase64();
            await using var runner = new UnitHostRunner(
                workingDirectory: tempDir.FullName,
                turnTimeout: TimeSpan.FromSeconds(5));

            var workerPid = await runner.PingAsync(ct);
            Assert.True(workerPid > 0);

            var workerEnv = await runner.GetWorkerEnvironmentAsync(ct);
            Assert.False(workerEnv.ContainsKey(secretEnvName));
            Assert.DoesNotContain(workerEnv.Values, v => v.Contains("top-secret-master-key-value", StringComparison.Ordinal));
            Assert.All(workerEnv.Keys, key => Assert.False(UnitHostRunner.IsSecretVariableName(key), $"Unexpected secret variable '{key}' in worker environment."));

            var deployed = await runner.DeployAsync(
                new DeployAppRequest(
                    BrainId: "local",
                    AppKey: "unit-counter",
                    Version: "1.0.0",
                    Digest: "sha256:test",
                    AssemblyBase64: assemblyBase64,
                    StateJson: "{\"value\":10}"),
                cancellationToken: ct);

            Assert.Equal("Unit Counter", deployed.Title);
            Assert.Contains(deployed.Commands, c => c.Name == "add");

            var dispatched = await runner.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "local",
                    AppKey: "unit-counter",
                    Kind: "command",
                    Name: "add",
                    PayloadJson: "{\"delta\":5}"),
                cancellationToken: ct);

            Assert.True(dispatched.Handled);
            Assert.Equal("15", dispatched.ResultJson);
            Assert.Contains("Count: 15", dispatched.UpdatedViewJson, StringComparison.Ordinal);

            // Now trigger an infinite `while (true)` turn with a 400ms watchdog override and verify clean kill!
            var spinningPid = runner.ActiveWorkerProcessId ?? workerPid;
            await Assert.ThrowsAsync<TimeoutException>(() => runner.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "local",
                    AppKey: "unit-counter",
                    Kind: "command",
                    Name: "spin-forever"),
                timeoutOverride: TimeSpan.FromMilliseconds(400),
                cancellationToken: ct));

            Assert.Null(runner.ActiveWorkerProcessId);
            AssertProcessExited(spinningPid);

            // Verify runner transparently recovers by spawning a fresh clean worker on the next turn.
            var recoveredPid = await runner.PingAsync(ct);
            Assert.True(recoveredPid > 0);
            Assert.NotEqual(spinningPid, recoveredPid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvName, null);
            tempDir.Delete(recursive: true);
        }
    }

    private static void AssertProcessExited(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            Assert.True(proc.HasExited || proc.WaitForExit(2_000), $"Process {pid} was still alive after confinement kill.");
        }
        catch (ArgumentException)
        {
            // Process is already gone from the OS process table.
        }
    }

    private static string CompileUnitAssemblyBase64()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(SampleUnitSource);
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        string[] neededSimpleNames =
        [
            "System.Private.CoreLib",
            "System.Runtime",
            "netstandard",
            "IntoChat.OS",
            "DigitalBrain",
            "DigitalBrain.UI",
        ];

        var references = new List<MetadataReference>();
        foreach (var path in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (neededSimpleNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Ensure IntoChat.OS, DigitalBrain, and DigitalBrain.UI are present even if loaded outside TPA.
        foreach (var type in (Type[])[typeof(IBrain), typeof(View), typeof(DigitalBrain.Signal)])
        {
            var location = type.Assembly.Location;
            if (!string.IsNullOrEmpty(location) && references.All(r => !string.Equals(r.Display, location, StringComparison.OrdinalIgnoreCase)))
            {
                references.Add(MetadataReference.CreateFromFile(location));
            }
        }

        var compilation = CSharpCompilation.Create(
            "SampleUnitHostTest_" + Guid.NewGuid().ToString("N")[..8],
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(
            emitResult.Success,
            "Failed to compile test unit assembly: " + string.Join(Environment.NewLine, emitResult.Diagnostics));

        return Convert.ToBase64String(peStream.ToArray());
    }
}
