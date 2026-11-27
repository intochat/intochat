using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DigitalBrain.Capabilities.Http;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Execution;

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

    [Fact]
    public void UnitHostConfinementSpecEnforcesLimitsZeroNetworkAndSeparatedLaunchCommand()
    {
        var tempAssemblyDir = Directory.CreateTempSubdirectory("intochat-unithost-spec-");
        var tempWorkDir = Directory.CreateTempSubdirectory("intochat-unithost-work-");
        try
        {
            var fakeAssembly = Path.Combine(tempAssemblyDir.FullName, "IntoChat.OS.Sandbox.dll");
            File.WriteAllText(fakeAssembly, string.Empty);

            var launchCommand = HttpSandboxRuntimeBridge.ResolveUnitHostLaunchCommand(fakeAssembly, tempAssemblyDir.FullName);
            Assert.False(string.IsNullOrWhiteSpace(launchCommand.FileName));
            Assert.Contains("--unit-host", launchCommand.Arguments);
            Assert.Contains("--contracts-dir", launchCommand.Arguments);
            Assert.Contains(fakeAssembly, launchCommand.Arguments);

            var spec = HttpSandboxRuntimeBridge.BuildUnitHostConfinementSpec(
                launchCommand,
                fakeAssembly,
                tempAssemblyDir.FullName,
                tempWorkDir.FullName,
                sandboxId: "unithost-spec-test");

            Assert.Equal("unithost-spec-test", spec.SandboxId);
            Assert.Equal(launchCommand.FileName, spec.ExecutablePath);
            Assert.Equal(launchCommand.Arguments, spec.Arguments);
            Assert.Equal(tempWorkDir.FullName, spec.WorkingDirectory);
            Assert.Contains(tempAssemblyDir.FullName, spec.ReadOnlyPaths);
            Assert.Empty(spec.ReadWritePaths);
            Assert.Equal(NetworkPolicy.None, spec.Network);
            Assert.Equal(1024L * 1024 * 1024, spec.MaxMemoryBytes);
            Assert.Equal(16, spec.MaxProcesses);
            Assert.True(spec.PreferAppContainer);
            Assert.Equal("0", spec.CleanEnvironment["SPIKE_WINDOWS"]);
        }
        finally
        {
            tempAssemblyDir.Delete(recursive: true);
            tempWorkDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HttpSandboxRuntimeBridgeResolvesPlatformConfinedProcessRunner()
    {
        await using var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .BuildServiceProvider();
        var options = Options.Create(new SandboxOptions
        {
            Url = string.Empty,
            TokenSigningMaterial = "unit-host-test-signing-material-0123456789abcdef",
        });

        await using var bridge = new HttpSandboxRuntimeBridge(
            services.GetRequiredService<IHttpClientFactory>(),
            options,
            new AppTokenService(options),
            services,
            services.GetRequiredService<ILogger<HttpSandboxRuntimeBridge>>());

        var runner = bridge.ResolveConfinedProcessRunner();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsAppContainerRunner>(runner);
        }
        else
        {
            Assert.IsType<PosixConfinedProcessRunner>(runner);
        }
    }

    [Fact]
    public void ChildEnvironmentPolicyBlocksServiceAssertionAndCustodySecrets()
    {
        Assert.True(ChildEnvironmentPolicy.IsBlockedSecretVariableName("INTOCHATOS_MASTER_KEY"));
        Assert.True(ChildEnvironmentPolicy.IsBlockedSecretVariableName("INTOCHAT_PRESENCE_TOKEN"));
        Assert.True(ChildEnvironmentPolicy.IsBlockedSecretVariableName("INTOCHAT_SERVICE_ASSERTION_SIGNING_KEY"));
        Assert.True(ChildEnvironmentPolicy.IsBlockedSecretVariableName("SERVICE_ASSERTION_JWT"));
        Assert.True(ChildEnvironmentPolicy.IsBlockedSecretVariableName("OPENAI_API_KEY"));
        Assert.False(ChildEnvironmentPolicy.IsBlockedSecretVariableName("SPIKE_WINDOWS"));
        Assert.False(ChildEnvironmentPolicy.IsBlockedSecretVariableName("SAFE_CUSTOM_FLAG"));
    }

    [Fact]
    public async Task UnitHostChildSessionUsesInjectedConfinedProcessRunnerAndCleansUpTempDirOnDispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturingRunner = new CapturingConfinedProcessRunner();

        await using var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .AddSingleton<IConfinedProcessRunner>(capturingRunner)
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

        string? workDir;
        try
        {
            var env = await bridge.ProbeUnitHostEnvironmentAsync(ct);
            Assert.Equal("0", env["SPIKE_WINDOWS"]);
            Assert.NotNull(capturingRunner.LastSpec);
            Assert.Equal(NetworkPolicy.None, capturingRunner.LastSpec!.Network);
            Assert.Equal(1024L * 1024 * 1024, capturingRunner.LastSpec.MaxMemoryBytes);
            Assert.Equal(16, capturingRunner.LastSpec.MaxProcesses);
            Assert.True(capturingRunner.LastSpec.PreferAppContainer);
            workDir = bridge.ActiveUnitHostWorkingDirectory;
            Assert.False(string.IsNullOrWhiteSpace(workDir));
            Assert.True(Directory.Exists(workDir));
        }
        finally
        {
            await bridge.DisposeAsync();
        }

        Assert.True(capturingRunner.LastSessionDisposed);
        Assert.False(Directory.Exists(workDir));
    }

    [Fact]
    public void WindowsAppContainerRunnerComputesUnconditionalJobLimitsEvenWhenSpecPassesZero()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Win32 Job Object limits require Windows."); return; }

        var zeroSpec = new ConfinementSpec(
            SandboxId: "sbx-zero-limits",
            ExecutablePath: "cmd.exe",
            Arguments: [],
            WorkingDirectory: Path.GetTempPath(),
            ReadOnlyPaths: [],
            ReadWritePaths: [],
            CleanEnvironment: new Dictionary<string, string>(),
            MaxMemoryBytes: 0,
            MaxProcesses: 0);

        var limits = WindowsAppContainerRunner.ComputeJobLimits(zeroSpec);
        const uint expectedFlags =
            0x00002000u // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | 0x00000400u // JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
            | 0x00000008u // JOB_OBJECT_LIMIT_ACTIVE_PROCESS
            | 0x00000200u; // JOB_OBJECT_LIMIT_JOB_MEMORY

        Assert.Equal(expectedFlags, limits.LimitFlags & expectedFlags);
        Assert.Equal(16u, limits.ActiveProcessLimit);
        Assert.Equal(1024UL * 1024UL * 1024UL, limits.JobMemoryLimitBytes);
        Assert.Equal(0xFFu, limits.UiRestrictionFlags);
    }

    [Fact]
    public async Task WindowsAppContainerRunnerFallsBackGracefullyWithDiagnosticsWhileEnforcingUnconditionalJobLimitsAndTempPurge()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Win32 AppContainer fallback test requires Windows."); return; }

        var ct = TestContext.Current.CancellationToken;
        var tempWorkDir = Directory.CreateTempSubdirectory("intochat-ac-fallback-");
        var logSink = new ListLogger<WindowsAppContainerRunner>();

        try
        {
            // Simulate a restricted Windows container/CI host where CreateAppContainerProfile returns E_ACCESSDENIED.
            var runner = new WindowsAppContainerRunner(
                logSink,
                static (_, _, _, _, _, out sid) =>
                {
                    sid = IntPtr.Zero;
                    return unchecked((int)0x80070005); // E_ACCESSDENIED
                });

            var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var spec = new ConfinementSpec(
                SandboxId: "sbx-ac-fallback",
                ExecutablePath: cmdExe,
                Arguments: ["/d", "/c", "more"],
                WorkingDirectory: tempWorkDir.FullName,
                ReadOnlyPaths: [],
                ReadWritePaths: [tempWorkDir.FullName],
                CleanEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SAFE_MODE"] = "fallback-verified",
                    ["SERVICE_ASSERTION_SIGNING_KEY"] = "must-not-leak",
                },
                PreferAppContainer: true,
                MaxMemoryBytes: 1024L * 1024 * 1024,
                MaxProcesses: 16);

            string sessionTempDir;
            await using (var session = runner.StartSession(spec))
            {
                Assert.Equal(ConfinementMode.JobObject, session.EffectiveMode);
                sessionTempDir = session.TempDirectory;
                Assert.True(Directory.Exists(sessionTempDir));

                // Verify live kernel Job Object enforces all 4 limits + 0xFF UI restrictions unconditionally.
                var windowsSession = Assert.IsType<WindowsAppContainerRunner.WindowsConfinedProcessSession>(session);
                var liveLimits = windowsSession.QueryJobLimits();
                Assert.Equal(16u, liveLimits.ActiveProcessLimit);
                Assert.Equal(1024UL * 1024UL * 1024UL, liveLimits.JobMemoryLimitBytes);
                Assert.Equal(0xFFu, liveLimits.UiRestrictionFlags);
                Assert.True((liveLimits.LimitFlags & 0x00002000u) != 0, "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE must be set.");
                Assert.True((liveLimits.LimitFlags & 0x00000200u) != 0, "JOB_OBJECT_LIMIT_JOB_MEMORY must be set.");
                Assert.True((liveLimits.LimitFlags & 0x00000008u) != 0, "JOB_OBJECT_LIMIT_ACTIVE_PROCESS must be set.");

                // Verify bidirectional 3-handle stdio pipe works cleanly.
                await session.StandardInput.WriteLineAsync("hello-fallback-pipe");
                await session.StandardInput.FlushAsync(ct);
                session.StandardInput.Close();
                var output = await session.StandardOutput.ReadToEndAsync(ct);
                Assert.Contains("hello-fallback-pipe", output, StringComparison.Ordinal);
            }

            // Verify per-session temp directory was recursively purged on DisposeAsync.
            Assert.False(Directory.Exists(sessionTempDir));

            // Verify structured warning diagnostic was logged explaining degraded isolation mode.
            Assert.Contains(
                logSink.Entries,
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("AppContainer isolation unavailable", StringComparison.Ordinal)
                    && e.Message.Contains("degraded JobObject confinement", StringComparison.Ordinal));
        }
        finally
        {
            tempWorkDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WindowsUnitHostExecutesConfinedWithScrubbedEnvAndPurgesTempDirsOnDispose()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows AppContainer UnitHost integration requires Windows."); return; }

        var ct = TestContext.Current.CancellationToken;
        var custodySecretVar = "INTOCHATOS_MASTER_KEY";
        var assertionSecretVar = "INTOCHAT_SERVICE_ASSERTION_SIGNING_KEY";
        var arbitraryParentVar = "UNITHOST_ARBITRARY_HOST_SECRET";
        var prevCustody = Environment.GetEnvironmentVariable(custodySecretVar);
        var prevAssertion = Environment.GetEnvironmentVariable(assertionSecretVar);
        Environment.SetEnvironmentVariable(custodySecretVar, "custody-secret-never-leak");
        Environment.SetEnvironmentVariable(assertionSecretVar, "assertion-signing-key-never-leak");
        Environment.SetEnvironmentVariable(arbitraryParentVar, "arbitrary-parent-secret-never-leak");

        var windowsRunner = new WindowsAppContainerRunner();
        var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .AddSingleton<IConfinedProcessRunner>(windowsRunner)
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

        string? tempDir = null;
        string? workDir = null;
        try
        {
            var childEnv = await bridge.ProbeUnitHostEnvironmentAsync(ct);

            Assert.True(
                bridge.ActiveUnitHostConfinementMode is ConfinementMode.AppContainer
                    or ConfinementMode.LessPrivilegedAppContainer
                    or ConfinementMode.JobObject,
                $"Unexpected Windows UnitHost confinement mode: {bridge.ActiveUnitHostConfinementMode}");

            tempDir = bridge.ActiveUnitHostTempDirectory;
            workDir = bridge.ActiveUnitHostWorkingDirectory;
            Assert.False(string.IsNullOrWhiteSpace(tempDir));
            Assert.False(string.IsNullOrWhiteSpace(workDir));
            Assert.True(Directory.Exists(tempDir));
            Assert.True(Directory.Exists(workDir));

            // Strict allowlist environment verification: zero custody keys, ServiceAssertion keys, or parent secrets.
            Assert.Equal("0", childEnv["SPIKE_WINDOWS"]);
            Assert.Equal(tempDir, childEnv["TEMP"], ignoreCase: true);
            Assert.DoesNotContain(custodySecretVar, childEnv.Keys);
            Assert.DoesNotContain(assertionSecretVar, childEnv.Keys);
            Assert.DoesNotContain(arbitraryParentVar, childEnv.Keys);
            Assert.DoesNotContain(childEnv.Values, static v => v.Contains("never-leak", StringComparison.Ordinal));
        }
        finally
        {
            await bridge.DisposeAsync();
            await services.DisposeAsync();
            Environment.SetEnvironmentVariable(custodySecretVar, prevCustody);
            Environment.SetEnvironmentVariable(assertionSecretVar, prevAssertion);
            Environment.SetEnvironmentVariable(arbitraryParentVar, null);
        }

        Assert.False(Directory.Exists(tempDir));
        Assert.False(Directory.Exists(workDir));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://192.168.1.5")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[fc00::1]")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://[::]")]
    [InlineData("https://[::ffff:169.254.169.254]")]
    [InlineData("https://user:pass@os.intochat.io")]
    [InlineData("http://os.intochat.io")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void SandboxCallbackUrlPolicyRejectsSsrfPrivateLoopbackUserInfoAndPlaintextByDefault(string url)
    {
        var ok = SandboxCallbackUrlPolicy.TryValidateAndNormalize(url, allowLoopback: false, out var origin, out var reason);

        Assert.False(ok, $"Expected '{url}' to be rejected by default, but got '{origin}'.");
        Assert.Equal(string.Empty, origin);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void SandboxCallbackUrlPolicyNormalizesPublicHttpsOriginAndHonorsLoopbackOptIn()
    {
        Assert.True(SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            "https://os.intochat.io/extra/path?x=1#frag",
            allowLoopback: false,
            out var publicOrigin,
            out var publicReason));
        Assert.Equal("https://os.intochat.io", publicOrigin);
        Assert.Null(publicReason);

        Assert.True(SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            "https://os.intochat.io:8443/callback?foo=bar",
            allowLoopback: false,
            out var portOrigin,
            out _));
        Assert.Equal("https://os.intochat.io:8443", portOrigin);

        // Explicit loopback opt-in permits 127.0.0.1 / localhost / [::1] (HTTP or HTTPS) normalized to origin.
        Assert.True(SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            "http://127.0.0.1:5080/edge/sandbox/invoke?q=1",
            allowLoopback: true,
            out var loopbackOrigin,
            out _));
        Assert.Equal("http://127.0.0.1:5080", loopbackOrigin);

        Assert.True(SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            "http://localhost:5080/extra",
            allowLoopback: true,
            out var localhostOrigin,
            out _));
        Assert.Equal("http://localhost:5080", localhostOrigin);

        Assert.True(SandboxCallbackUrlPolicy.TryValidateAndNormalize(
            "https://[::1]:5081/extra",
            allowLoopback: true,
            out var ipv6LoopbackOrigin,
            out _));
        Assert.Equal("https://[::1]:5081", ipv6LoopbackOrigin);

        // Even with AllowLoopbackKernelCallback=true, metadata, RFC1918/ULA, UserInfo, and non-loopback plaintext HTTP remain blocked.
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("http://169.254.169.254/latest/meta-data", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("https://10.0.0.1", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("https://172.16.0.1", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("https://192.168.1.5", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("https://[fe80::1]", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("https://[fc00::1]", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("http://user:pass@127.0.0.1:5080", allowLoopback: true, out _, out _));
        Assert.False(SandboxCallbackUrlPolicy.TryValidateAndNormalize("http://os.intochat.io", allowLoopback: true, out _, out _));
    }

    [Fact]
    public async Task SandboxCallbackUrlPolicyBlocksDnsRebindingToPrivateMetadataAndLoopbackAtConnectTime()
    {
        var ct = TestContext.Current.CancellationToken;

        // Public-only handler refuses DNS rebinding to 169.254.169.254, 10.0.0.1, or 127.0.0.1.
        using var publicHandler = SandboxCallbackUrlPolicy.CreateRemoteCallbackHandler(
            allowLoopback: false,
            resolver: static (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("169.254.169.254"),
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("10.0.0.1"),
            }));
        Assert.False(publicHandler.AllowAutoRedirect);

        using var publicClient = new HttpClient(publicHandler, disposeHandler: false);
        var publicEx = await Assert.ThrowsAsync<HttpRequestException>(
            () => publicClient.GetAsync("https://rebind.example.test/edge/sandbox/invoke", ct));
        Assert.IsType<HostNotAllowedException>(publicEx.InnerException);

        // Even with allowLoopback=true, DNS rebinding to link-local metadata / RFC1918 is refused.
        using var loopbackHandler = SandboxCallbackUrlPolicy.CreateRemoteCallbackHandler(
            allowLoopback: true,
            resolver: static (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("169.254.169.254"),
                IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("fe80::1"),
            }));
        using var loopbackClient = new HttpClient(loopbackHandler, disposeHandler: false);
        var loopbackEx = await Assert.ThrowsAsync<HttpRequestException>(
            () => loopbackClient.GetAsync("https://rebind-meta.example.test/edge/sandbox/invoke", ct));
        Assert.IsType<HostNotAllowedException>(loopbackEx.InnerException);
    }

    [Fact]
    public async Task HttpSandboxRuntimeBridgeRefCountsOverlappingRemoteCallbacksAndFailsClosedOnBlockedOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        var logSink = new ListLogger<HttpSandboxRuntimeBridge>();
        await using var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .BuildServiceProvider();
        var options = Options.Create(new SandboxOptions
        {
            Url = string.Empty,
            TokenSigningMaterial = "unit-host-test-signing-material-0123456789abcdef",
            AllowLoopbackKernelCallback = false,
        });

        await using var bridge = new HttpSandboxRuntimeBridge(
            services.GetRequiredService<IHttpClientFactory>(),
            options,
            new AppTokenService(options),
            services,
            logSink);

        const string key = "brain-a/app/same-app";
        var reg1 = bridge.RegisterRemoteCallback(key, "https://os-a.intochat.io", null);
        var reg2 = bridge.RegisterRemoteCallback(key, "https://os-b.intochat.io", null);

        Assert.True(bridge.TryGetActiveRemoteCallback(key, out var activeWhileBoth));
        Assert.Equal("https://os-b.intochat.io", activeWhileBoth.CanonicalOrigin);

        // Completing job 1 must not clear job 2's active callback registration.
        bridge.UnregisterRemoteCallback(key, reg1);
        Assert.True(bridge.TryGetActiveRemoteCallback(key, out var activeAfterFirst));
        Assert.Equal("https://os-b.intochat.io", activeAfterFirst.CanonicalOrigin);

        bridge.UnregisterRemoteCallback(key, reg2);
        Assert.False(bridge.TryGetActiveRemoteCallback(key, out _));

        // Verify fail-closed 403 Forbidden when a rejected KernelCallbackUrl is forwarded over pipe callback.
        var rejectedRunner = new CallbackInvokingConfinedProcessRunner();
        await using var guardedBridge = new HttpSandboxRuntimeBridge(
            services.GetRequiredService<IHttpClientFactory>(),
            options,
            new AppTokenService(options),
            services,
            logSink,
            confinedProcessRunner: rejectedRunner);

        IntoChat.OS.Modules.Devices.ILocalSandboxExecutor executor = guardedBridge;
        var response = await executor.ExecuteAsync(
            new IntoChat.OS.Modules.Devices.ConnectedSandboxRequest(
                JobId: "ssrf-job-1",
                BrainId: "brain-ssrf",
                AppKey: "app-ssrf",
                Version: "1.0.0",
                Digest: "abc",
                AssemblyBase64: Convert.ToBase64String([1, 2, 3]),
                Command: "run",
                PayloadJson: "{}",
                StateJson: null,
                SettingsJson: null,
                KernelCallbackUrl: "http://169.254.169.254/latest/meta-data",
                AppToken: "tok",
                AllowedContracts: []),
            ct);

        Assert.Equal(403, rejectedRunner.LastCallbackStatusCode);
        Assert.False(rejectedRunner.LastCallbackOk ?? true);
        Assert.Contains(
            logSink.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("Rejected remote KernelCallbackUrl", StringComparison.Ordinal));
    }

    private sealed class CallbackInvokingConfinedProcessRunner : IConfinedProcessRunner
    {
        public int? LastCallbackStatusCode { get; private set; }
        public bool? LastCallbackOk { get; private set; }

        public Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ConfinedProcessSession StartSession(ConfinementSpec spec)
            => new CallbackSession(this);

        private sealed class CallbackSession : ConfinedProcessSession
        {
            private readonly StreamWriter _stdin;
            private readonly StreamReader _stdout;
            private readonly StreamReader _stderr;
            private readonly StreamWriter _stdoutFeed;
            private readonly string _tempDir;
            private bool _exited;

            public CallbackSession(CallbackInvokingConfinedProcessRunner owner)
            {
                _tempDir = Directory.CreateTempSubdirectory("intochat-cb-session-").FullName;

                var stdoutPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
                var stdoutClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, stdoutPipe.GetClientHandleAsString());
                _stdoutFeed = new StreamWriter(stdoutPipe, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                _stdout = new StreamReader(stdoutClient, System.Text.Encoding.UTF8);
                _stderr = new StreamReader(Stream.Null);

                var stdinPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
                var stdinClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, stdinPipe.GetClientHandleAsString());
                _stdin = new StreamWriter(stdinPipe, new System.Text.UTF8Encoding(false));
                var stdinReader = new StreamReader(stdinClient, System.Text.Encoding.UTF8);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? pendingDispatchId = null;
                        while (await stdinReader.ReadLineAsync().ConfigureAwait(false) is { } line)
                        {
                            using var doc = JsonDocument.Parse(line);
                            var id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            var op = doc.RootElement.GetProperty("op").GetString() ?? string.Empty;
                            if (op == "ping")
                            {
                                await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, processId = 515151 })).ConfigureAwait(false);
                            }
                            else if (op == "deploy")
                            {
                                var deployResp = new
                                {
                                    id,
                                    ok = true,
                                    deploy = new
                                    {
                                        brainId = "brain-ssrf",
                                        appKey = "app-ssrf",
                                        version = "1.0.0",
                                        digest = "abc",
                                        title = "SSRF Probe",
                                        summary = "SSRF Probe",
                                        commands = Array.Empty<object>(),
                                        signals = Array.Empty<string>(),
                                        schedules = Array.Empty<string>(),
                                    },
                                };
                                await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(deployResp)).ConfigureAwait(false);
                            }
                            else if (op == "dispatch")
                            {
                                pendingDispatchId = id;
                                var cbFrame = new
                                {
                                    id = "cb-ssrf-1",
                                    ok = true,
                                    callbackKind = "invoke",
                                    brainId = "brain-ssrf",
                                    appKey = "app-ssrf",
                                    appToken = "tok",
                                    invoke = new { contract = "IntoChat.Sdk.INotifications", method = "Send", args = Array.Empty<object>() },
                                };
                                await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(cbFrame)).ConfigureAwait(false);
                            }
                            else if (op == "callback-reply")
                            {
                                owner.LastCallbackStatusCode = doc.RootElement.TryGetProperty("statusCode", out var sc) ? sc.GetInt32() : null;
                                owner.LastCallbackOk = doc.RootElement.TryGetProperty("ok", out var okProp) ? okProp.GetBoolean() : null;
                                if (pendingDispatchId is not null)
                                {
                                    var dispResp = new
                                    {
                                        id = pendingDispatchId,
                                        ok = true,
                                        dispatch = new { handled = false, error = "callback blocked" },
                                    };
                                    await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(dispResp)).ConfigureAwait(false);
                                    pendingDispatchId = null;
                                }
                            }
                            else if (op == "unload")
                            {
                                await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, unload = new { unloaded = true, brainId = "brain-ssrf", appKey = "app-ssrf" } })).ConfigureAwait(false);
                            }
                            else if (op == "shutdown")
                            {
                                await _stdoutFeed.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, processId = 515151 })).ConfigureAwait(false);
                                _exited = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                        Trace.TraceInformation("CallbackSession reader closed cleanly: {0}", ex.Message);
                    }
                    finally
                    {
                        _stdoutFeed.Dispose();
                        stdinReader.Dispose();
                    }
                });
            }

            public override int ProcessId => 515151;
            public override ConfinementMode EffectiveMode => ConfinementMode.JobObject;
            public override string TempDirectory => _tempDir;
            public override StreamWriter StandardInput => _stdin;
            public override StreamReader StandardOutput => _stdout;
            public override StreamReader StandardError => _stderr;
            public override bool HasExited => _exited;
            public override int ExitCode => 0;
            public override Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public override void Kill() => _exited = true;

            public override ValueTask DisposeAsync()
            {
                _exited = true;
                _stdin.Dispose();
                _stdout.Dispose();
                _stderr.Dispose();
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CapturingConfinedProcessRunner : IConfinedProcessRunner
    {
        public ConfinementSpec? LastSpec { get; private set; }
        public bool LastSessionDisposed { get; private set; }

        public Task<ConfinedProcessResult> RunAsync(ConfinementSpec spec, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ConfinedProcessSession StartSession(ConfinementSpec spec)
        {
            LastSpec = spec;
            return new FakeUnitHostSession(() => LastSessionDisposed = true, spec.CleanEnvironment);
        }

        private sealed class FakeUnitHostSession : ConfinedProcessSession
        {
            private readonly Action _onDispose;
            private readonly MemoryStream _inBuffer = new();
            private readonly StreamWriter _stdin;
            private readonly StreamReader _stdout;
            private readonly StreamReader _stderr;
            private readonly StreamWriter _stdoutFeed;
            private readonly string _tempDir;
            private bool _exited;

            public FakeUnitHostSession(Action onDispose, IReadOnlyDictionary<string, string> env)
            {
                _onDispose = onDispose;
                _tempDir = Directory.CreateTempSubdirectory("intochat-fake-session-").FullName;

                var stdoutPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
                var stdoutClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, stdoutPipe.GetClientHandleAsString());
                _stdoutFeed = new StreamWriter(stdoutPipe, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                _stdout = new StreamReader(stdoutClient, System.Text.Encoding.UTF8);
                _stderr = new StreamReader(Stream.Null);

                var stdinPipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
                var stdinClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, stdinPipe.GetClientHandleAsString());
                _stdin = new StreamWriter(stdinPipe, new System.Text.UTF8Encoding(false));
                var stdinReader = new StreamReader(stdinClient, System.Text.Encoding.UTF8);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (await stdinReader.ReadLineAsync().ConfigureAwait(false) is { } line)
                        {
                            using var doc = JsonDocument.Parse(line);
                            var id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            var op = doc.RootElement.GetProperty("op").GetString() ?? string.Empty;
                            if (op == "ping")
                            {
                                var response = JsonSerializer.Serialize(new { id, ok = true, processId = 424242 });
                                await _stdoutFeed.WriteLineAsync(response).ConfigureAwait(false);
                            }
                            else if (op == "env")
                            {
                                var response = JsonSerializer.Serialize(new
                                {
                                    id,
                                    ok = true,
                                    environmentSnapshot = env,
                                    processId = 424242,
                                });
                                await _stdoutFeed.WriteLineAsync(response).ConfigureAwait(false);
                            }
                            else if (op == "shutdown")
                            {
                                var response = JsonSerializer.Serialize(new { id, ok = true, processId = 424242 });
                                await _stdoutFeed.WriteLineAsync(response).ConfigureAwait(false);
                                _exited = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                        Trace.TraceInformation("FakeUnitHostSession reader closed cleanly: {0}", ex.Message);
                    }
                    finally
                    {
                        _stdoutFeed.Dispose();
                        stdinReader.Dispose();
                    }
                });
            }

            public override int ProcessId => 424242;
            public override ConfinementMode EffectiveMode => ConfinementMode.AppContainer;
            public override string TempDirectory => _tempDir;
            public override StreamWriter StandardInput => _stdin;
            public override StreamReader StandardOutput => _stdout;
            public override StreamReader StandardError => _stderr;
            public override bool HasExited => _exited;
            public override int ExitCode => 0;
            public override Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public override void Kill() => _exited = true;

            public override ValueTask DisposeAsync()
            {
                _exited = true;
                _stdin.Dispose();
                _stdout.Dispose();
                _stderr.Dispose();
                _inBuffer.Dispose();
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }

                _onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task ResolveKernelCallbackUrlPreservesContainerUrlUnlessExplicitlyConfigured()
    {
        await using var services = new ServiceCollection()
            .AddHttpClient()
            .AddLogging()
            .AddSingleton<IServer>(new StubServer("http://localhost:17990"))
            .BuildServiceProvider();

        HttpSandboxRuntimeBridge CreateBridge(string url, string? callbackUrl = null)
        {
            var options = Options.Create(new SandboxOptions
            {
                Url = url,
                KernelCallbackUrl = callbackUrl,
                TokenSigningMaterial = "unit-host-test-signing-material-0123456789abcdef",
            });
            return new HttpSandboxRuntimeBridge(
                services.GetRequiredService<IHttpClientFactory>(),
                options,
                new AppTokenService(options),
                services,
                services.GetRequiredService<ILogger<HttpSandboxRuntimeBridge>>());
        }

        await using var remoteDefault = CreateBridge("http://localhost:18080");
        await using var remoteOverride = CreateBridge("http://localhost:18080", "http://127.0.0.1:5080");
        await using var hostedPublic = CreateBridge("http://intochat-test-sandbox", "https://os.test.intochat.io/");
        await using var localUnitHost = CreateBridge(string.Empty);

        Assert.Null(remoteDefault.ResolveKernelCallbackUrl());
        Assert.Equal("http://127.0.0.1:5080", remoteOverride.ResolveKernelCallbackUrl());
        Assert.Equal("https://os.test.intochat.io", hostedPublic.ResolveKernelCallbackUrl());
        Assert.Equal("http://localhost:17990", localUnitHost.ResolveKernelCallbackUrl());
    }

    [Fact]
    public void ResolveUnitHostLaunchCommandPrefersNativeApphostWhenPresentAndFallsBackToDotNetExec()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-unithost-launch-");
        try
        {
            var dllPath = Path.Combine(tempDir.FullName, "IntoChat.OS.Sandbox.dll");
            File.WriteAllText(dllPath, "stub-dll");

            // 1. Only .dll present -> falls back to `dotnet exec <dll> --unit-host --contracts-dir <dir>`
            var devFallback = HttpSandboxRuntimeBridge.ResolveUnitHostLaunchCommand(dllPath, "C:\\contracts", isWindows: true);
            Assert.Contains("dotnet", Path.GetFileNameWithoutExtension(devFallback.FileName), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["exec", dllPath, "--unit-host", "--contracts-dir", "C:\\contracts"], devFallback.Arguments);

            // 2. Windows apphost .exe present alongside .dll -> launches .exe directly without `dotnet exec`
            var exePath = Path.Combine(tempDir.FullName, "IntoChat.OS.Sandbox.exe");
            File.WriteAllText(exePath, "stub-exe");
            var windowsApphost = HttpSandboxRuntimeBridge.ResolveUnitHostLaunchCommand(dllPath, "C:\\contracts", isWindows: true);
            Assert.Equal(Path.GetFullPath(exePath), windowsApphost.FileName);
            Assert.Equal(["--unit-host", "--contracts-dir", "C:\\contracts"], windowsApphost.Arguments);

            // 3. Passing the .exe directly also launches without `dotnet exec`
            var directExe = HttpSandboxRuntimeBridge.ResolveUnitHostLaunchCommand(exePath, string.Empty, isWindows: true);
            Assert.Equal(Path.GetFullPath(exePath), directExe.FileName);
            Assert.Equal(["--unit-host"], directExe.Arguments);

            // 4. POSIX native apphost present alongside .dll -> launches native binary directly
            var posixPath = Path.Combine(tempDir.FullName, "IntoChat.OS.Sandbox");
            File.WriteAllText(posixPath, "stub-elf");
            var posixApphost = HttpSandboxRuntimeBridge.ResolveUnitHostLaunchCommand(dllPath, "/contracts", isWindows: false);
            Assert.Equal(Path.GetFullPath(posixPath), posixApphost.FileName);
            Assert.Equal(["--unit-host", "--contracts-dir", "/contracts"], posixApphost.Arguments);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class StubServer : IServer
    {
        public StubServer(params string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
            {
                feature.Addresses.Add(address);
            }
            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
