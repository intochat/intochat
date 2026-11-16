using System.Diagnostics;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

// The dry run of eng/release/package-release.ps1 is what ci runs between releases. It must check that everything the
// real run publishes is still where the script looks, or a moved project is found only on release day.
public sealed class PackageReleaseInputFacts
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static (int ExitCode, string Output) DryRun(string script, params string[] extra)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Rid", "win-x64", "-DryRun", .. extra])
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pwsh.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("package-release.ps1 -DryRun timed out after 60s.");
        }
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void DryRunFindsEveryInputInThisRepository()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var run = DryRun(Path.Combine(RepoRoot(), "eng", "release", "package-release.ps1"));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("IntoChat.OS.Desktop.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Sandbox.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("pubspec.yaml", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunFailsAndNamesAnInputThatIsNoLongerThere()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A copy of the script in a tree that has a VERSION and nothing to publish.
        var temp = Directory.CreateTempSubdirectory("intochat-pkg-inputs-");
        try
        {
            var release = Directory.CreateDirectory(Path.Combine(temp.FullName, "eng", "release"));
            var script = Path.Combine(release.FullName, "package-release.ps1");
            File.Copy(Path.Combine(RepoRoot(), "eng", "release", "package-release.ps1"), script);
            File.WriteAllText(Path.Combine(temp.FullName, "VERSION"), "9.9.9\n");

            var run = DryRun(script);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("IntoChat.OS.Desktop.csproj", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
