using IntoChat.OS.LocalSetup;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

// The dry run of ReleasePackager is what ci runs between releases. It must check that everything the
// real run publishes is still where the packager looks, or a moved project is found only on release day.
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

    private static async Task<(int ExitCode, string Output)> DryRunAsync(string repoRoot)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await ReleasePackager.RunAsync(new ReleasePackageOptions
        {
            RepoRoot = repoRoot,
            Rid = "win-x64",
            DryRun = true,
        }, stdout, stderr);
        return (exitCode, stdout.ToString() + stderr.ToString());
    }

    [Fact]
    public async Task DryRunFindsEveryInputInThisRepository()
    {
        var run = await DryRunAsync(RepoRoot());

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("IntoChat.OS.Desktop.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Kernel.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("IntoChat.OS.Sandbox.csproj", run.Output, StringComparison.Ordinal);
        Assert.Contains("pubspec.yaml", run.Output, StringComparison.Ordinal);
        Assert.Contains("Native AOT", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunFailsAndNamesAnInputThatIsNoLongerThere()
    {
        // A tree that has a VERSION and nothing to publish.
        var temp = Directory.CreateTempSubdirectory("intochat-pkg-inputs-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp.FullName, "VERSION"), "9.9.9\n", TestContext.Current.CancellationToken);

            var run = await DryRunAsync(temp.FullName);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("IntoChat.OS.Desktop.csproj", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
