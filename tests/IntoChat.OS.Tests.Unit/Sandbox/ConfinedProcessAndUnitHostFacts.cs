using System.Diagnostics;
using IntoChat.OS.Framework.Execution;

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

}
