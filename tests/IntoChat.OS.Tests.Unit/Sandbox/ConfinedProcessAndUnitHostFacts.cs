using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public sealed class ConfinedProcessAndUnitHostFacts
{
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
        if (!OperatingSystem.IsWindows()) { Assert.Skip("AppContainer isolation requires Windows."); return; }

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
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Job object timeout enforcement requires Windows."); return; }

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

    [Fact]
    public async Task HttpSandboxRuntimeBridgeWithoutUrlExecutesOutOfProcessInUnitHost()
    {
        var ct = TestContext.Current.CancellationToken;
        const string sourceCode = """
            using System;
            using DigitalBrain.UI;
            using IntoChat.Sdk;

            namespace ProbeAppNs;

            public sealed class ProbeState
            {
                public int LastPid { get; set; }
                public int Invocations { get; set; }
            }

            public sealed class ProbeSettings
            {
                public string Tag { get; set; } = "unit-host";
            }

            [App("probe-outofproc", Title = "Probe Out Of Proc", Summary = "Proves unit-host execution")]
            public sealed class ProbeApp : BrainApp<ProbeState, ProbeSettings>
            {
                [OnCommand("probe", Description = "Returns child PID")]
                public int Probe(int delta = 1)
                {
                    State.LastPid = Environment.ProcessId;
                    State.Invocations += delta;
                    return State.LastPid;
                }

                public override View Render()
                    => new("Probe", new Text($"pid={State.LastPid};calls={State.Invocations};tag={Settings.Tag}"));
            }
            """;

        var compiler = new AppPackageCompiler();
        var package = new AppPackage(
            Key: "probe-outofproc",
            Version: "1.0.0",
            Title: "Probe Out Of Proc",
            Summary: "Proves unit-host execution",
            Icon: "apps",
            Category: "Test",
            Author: "test",
            SourceFiles: [new AppSourceFile("ProbeApp.cs", sourceCode)],
            Needs: new AppPackageNeeds([], [], [], []),
            DefaultSettingsJson: "{}",
            Digest: string.Empty);

        var compiled = compiler.Compile(package);
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));

        var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .AddSingleton<SdkAiAdapter>()
            .AddSingleton<SdkTablesAdapter>()
            .BuildServiceProvider();
        var options = Options.Create(new SandboxOptions
        {
            Url = string.Empty,
            TokenSigningMaterial = "unit-host-test-signing-material-0123456789abcdef",
        });
        var bridge = new HttpSandboxRuntimeBridge(
            services.GetRequiredService<IHttpClientFactory>(),
            options,
            new AppTokenService(options),
            services,
            services.GetRequiredService<ILogger<HttpSandboxRuntimeBridge>>());

        try
        {
            Assert.IsAssignableFrom<IAsyncDisposable>(bridge);
            var deploy = await bridge.DeployPackageAsync(
                "brain-probe",
                compiled.EnrichedPackage!,
                compiled.AssemblyBytes!,
                stateJson: null,
                settingsJson: null,
                cancellationToken: ct);

            Assert.True(deploy.Succeeded, deploy.Error);
            var dispatch = await bridge.DispatchCommandAsync("brain-probe", "probe-outofproc", "probe", "{\"delta\":2}", ct);
            Assert.True(dispatch.Handled, dispatch.Error);
            Assert.NotNull(dispatch.ResultJson);

            var childPid = JsonSerializer.Deserialize<int>(dispatch.ResultJson!, JsonSerializerOptions.Web);
            Assert.True(childPid > 0);
            Assert.NotEqual(Environment.ProcessId, childPid);

            var rendered = await bridge.RenderAsync("brain-probe", "probe-outofproc", ct);
            Assert.NotNull(rendered);
            var viewJson = JsonSerializer.Serialize(rendered, JsonSerializerOptions.Web);
            Assert.Contains($"pid={childPid};calls=2;tag=unit-host", viewJson, StringComparison.Ordinal);

            await bridge.UnloadAsync("brain-probe", "probe-outofproc", ct);
        }
        finally
        {
            if ((object)bridge is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if ((object)bridge is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await services.DisposeAsync();
        }
    }
}
