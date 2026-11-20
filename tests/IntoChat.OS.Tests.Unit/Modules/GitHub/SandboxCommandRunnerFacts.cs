using System.Diagnostics;
using System.Security;
using System.Text;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Host.Execution;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class SandboxCommandRunnerFacts : IDisposable
{
    private readonly string _root;

    public SandboxCommandRunnerFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "intochat-sbx-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<WorkTreeLease> PrepareContainerVolumeLeaseAsync(string subName)
    {
        var subRoot = Path.Combine(_root, subName);
        var bareOrigin = Path.Combine(subRoot, "origin.git");
        var checkout = Path.Combine(subRoot, "repo");
        var home = Path.Combine(subRoot, "home");
        var volumes = Path.Combine(subRoot, "volumes");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(home);

        RunProcess("git", ["init", "--bare", "--initial-branch=main"], bareOrigin);
        RunProcess("git", ["init", "--initial-branch=main"], checkout);
        RunProcess("git", ["config", "user.name", "Test Owner"], checkout);
        RunProcess("git", ["config", "user.email", "owner@example.test"], checkout);

        Directory.CreateDirectory(Path.Combine(checkout, "code"));
        Directory.CreateDirectory(Path.Combine(checkout, "ui"));
        await File.WriteAllTextAsync(Path.Combine(checkout, "code", "Sample.cs"), "namespace Demo;\n\npublic static class Sample { }\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(checkout, "ui", "pubspec.yaml"), "name: sample_ui\n", Ct);

        RunProcess("git", ["add", "."], checkout);
        RunProcess("git", ["commit", "-m", "Initial commit"], checkout);
        RunProcess("git", ["remote", "add", "origin", bareOrigin], checkout);
        RunProcess("git", ["push", "-u", "origin", "main"], checkout);

        var service = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VolumeMountRoot = volumes,
            Backend = WorkTreeBackendKind.ContainerVolume,
        });
        service.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main", RemoteCloneUrl: bareOrigin));

        return await service.PrepareAsync(RepositoryId.Parse("os"), "sbxrun01", "Verify container sandbox", null, Ct);
    }

    [Fact]
    public async Task BuildContainerArgumentsBindsWorkspaceMountEnforcesNetworkPolicyAndTranslatesHostPaths()
    {
        var lease = await PrepareContainerVolumeLeaseAsync("args");
        var runner = ISandboxCommandRunner.CreateDefault(new SandboxCommandOptions
        {
            ContainerImage = "sandbox/sdk:11.0",
            NetworkPolicy = SandboxNetworkPolicy.None,
        });

        var hostCsproj = Path.Combine(lease.WorkspacePath, "code", "Sample.csproj");
        var args = runner.BuildContainerArguments(
            lease,
            new SandboxCommandRequest("dotnet", ["build", hostCsproj, "-c", "Release"]));

        var fullWorkspace = Path.GetFullPath(lease.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Contains("no-new-privileges", args);

        var netIdx = args.ToList().IndexOf("--network");
        Assert.True(netIdx >= 0);
        Assert.Equal("none", args[netIdx + 1]);

        var volIdx = args.ToList().IndexOf("-v");
        Assert.True(volIdx >= 0);
        Assert.Equal($"{fullWorkspace}:/workspace:rw", args[volIdx + 1]);

        var workDirIdx = args.ToList().IndexOf("-w");
        Assert.True(workDirIdx >= 0);
        Assert.Equal("/workspace", args[workDirIdx + 1]);

        Assert.Contains("sandbox/sdk:11.0", args);
        Assert.Contains("/workspace/code/Sample.csproj", args);

        var flutterArgs = runner.BuildContainerArguments(
            lease,
            new SandboxCommandRequest(
                "flutter",
                ["analyze", "--no-pub"],
                RelativeWorkingDirectory: "ui",
                NetworkPolicy: SandboxNetworkPolicy.Bridge));
        var bridgeIdx = flutterArgs.ToList().IndexOf("--network");
        Assert.Equal("bridge", flutterArgs[bridgeIdx + 1]);
        var uiWorkDirIdx = flutterArgs.ToList().IndexOf("-w");
        Assert.Equal("/workspace/ui", flutterArgs[uiWorkDirIdx + 1]);

        Assert.Throws<ArgumentException>(() => runner.BuildContainerArguments(
            lease,
            new SandboxCommandRequest("dotnet", ["build"], RelativeWorkingDirectory: "../escape")));

        Assert.Throws<SecurityException>(() => runner.BuildContainerArguments(
            lease,
            new SandboxCommandRequest("../bin/sh", ["-c", "whoami"])));
    }

    [Fact]
    public async Task ExecuteAsyncInContainerModeInvokesContainerRuntimeAndCapturesOutput()
    {
        var lease = await PrepareContainerVolumeLeaseAsync("container-exec");
        var fakeCliDir = BuildScriptedContainerRuntime(out var logPath);

        var runner = ISandboxCommandRunner.CreateDefault(new SandboxCommandOptions
        {
            Mode = SandboxExecutionMode.Container,
            ContainerRuntime = "fake-container-cli",
            ContainerImage = "sandbox/sdk:11.0",
            NetworkPolicy = SandboxNetworkPolicy.None,
            ToolPath = fakeCliDir,
        });

        var result = await runner.ExecuteAsync(
            lease,
            new SandboxCommandRequest("dotnet", ["build", "code/Sample.csproj", "-c", "Release"]),
            Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(SandboxExecutionMode.Container, result.ExecutedMode);
        Assert.Contains("Build succeeded.", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("sandbox-stderr-trace", result.StdErr, StringComparison.Ordinal);
        Assert.Equal("fake-container-cli", result.EffectiveInvocation[0]);

        var recorded = await File.ReadAllTextAsync(logPath, Ct);
        Assert.Contains("run|--rm|--network|none", recorded, StringComparison.Ordinal);
        Assert.Contains(":/workspace:rw|-w|/workspace|", recorded, StringComparison.Ordinal);
        Assert.Contains("sandbox/sdk:11.0|dotnet|build|code/Sample.csproj|-c|Release", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncInAutoModeRunsConfinedHostWithoutContainerDaemonAndVerifiesTripleGitLock()
    {
        var lease = await PrepareContainerVolumeLeaseAsync("auto-host");
        var runner = ISandboxCommandRunner.CreateDefault();

        var ok = await runner.ExecuteAsync(
            lease,
            new SandboxCommandRequest("dotnet", ["--version"]),
            Ct);

        Assert.True(ok.Succeeded);
        Assert.Equal(SandboxExecutionMode.ConfinedHost, ok.ExecutedMode);
        Assert.False(string.IsNullOrWhiteSpace(ok.StdOut));

        var outsidePath = Path.Combine(_root, "outside.csproj");
        await Assert.ThrowsAsync<SecurityException>(() => runner.ExecuteAsync(
            lease,
            new SandboxCommandRequest("dotnet", ["build", outsidePath]),
            Ct));

        var dotGit = Path.Combine(lease.WorkspacePath, ".git");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dotGit, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.SetAttributes(dotGit, FileAttributes.Normal);
        await File.WriteAllTextAsync(dotGit, "gitdir: " + Path.Combine(_root, "tampered") + "\n", Encoding.UTF8, Ct);

        await Assert.ThrowsAsync<SecurityException>(() => runner.ExecuteAsync(
            lease,
            new SandboxCommandRequest("dotnet", ["--version"]),
            Ct));
    }

    private string BuildScriptedContainerRuntime(out string logPath)
    {
        var toolSrc = Path.Combine(_root, "cli-src");
        var toolBin = Path.Combine(_root, "cli-bin");
        Directory.CreateDirectory(toolSrc);
        Directory.CreateDirectory(toolBin);

        logPath = Path.Combine(_root, "container-cli.log");
        var escapedLog = logPath.Replace("\\", "\\\\", StringComparison.Ordinal);

        File.WriteAllText(
            Path.Combine(toolSrc, "fake-container-cli.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>");

        var program = $$"""
            using System;
            using System.IO;

            internal static class Program
            {
                public static int Main(string[] args)
                {
                    File.AppendAllText("{{escapedLog}}", string.Join("|", args) + "\n");
                    Console.WriteLine("Build succeeded.\n    0 Warning(s)\n    0 Error(s)");
                    Console.Error.WriteLine("sandbox-stderr-trace");
                    return 0;
                }
            }
            """;
        File.WriteAllText(Path.Combine(toolSrc, "Program.cs"), program);
        RunProcess("dotnet", ["build", Path.Combine(toolSrc, "fake-container-cli.csproj"), "-c", "Release", "-o", toolBin, "-v", "q"], toolSrc);
        return toolBin;
    }

    private static string RunProcess(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}\n{stdout}");
        }

        return stdout;
    }
}
