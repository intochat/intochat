using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Aspire.Hosting.Tests.Unit;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class LocalCheckoutRegistrationFacts : IDisposable
{
    private readonly string _tempHome;

    public LocalCheckoutRegistrationFacts()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "intochat-local-checkout-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempHome))
            {
                Directory.Delete(_tempHome, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void LocalTopology_EnablesRoslynModule()
    {
        Assert.DoesNotContain("roslyn", IntoChatOSNames.LocalDisabledModules, StringComparer.OrdinalIgnoreCase);

        var configuration = new ConfigurationManager();
        LocalRunDefaults.Apply(configuration);
        Assert.True(LocalRunDefaults.IsEnabled(configuration, "roslyn"));

        var services = new ServiceCollection();
        AppsModule.ConfigureServices(services, configuration);
        RoslynModule.ConfigureServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        var roslyn = provider.GetRequiredService<IWorktreeRoslyn>();

        Assert.IsType<WorktreeRoslynAnalyzer>(roslyn);
        Assert.IsNotType<UnavailableWorktreeRoslyn>(roslyn);
    }

    [Fact]
    public async Task LocalTopology_RegistersCheckoutAsOsRepository()
    {
        var env = await ResolveKernelEnvironmentAsync("local");

        Assert.True(
            env.TryGetValue("IntoChatOS__Repositories__os", out var rawPath),
            "Expected local kernel environment to project IntoChatOS__Repositories__os to the git checkout root.");
        var checkoutRoot = Assert.IsType<string>(rawPath);
        Assert.False(string.IsNullOrWhiteSpace(checkoutRoot));

        var gitMarker = Path.Combine(checkoutRoot, ".git");
        Assert.True(Directory.Exists(gitMarker) || File.Exists(gitMarker), $"Expected '{checkoutRoot}' to be a git checkout root.");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Repositories:os"] = checkoutRoot,
            })
            .Build();

        var workTree = new WorkTreeService(configuration, new WorkTreeOptions { HomeDirectory = _tempHome });
        var repos = workTree.ListRepositories();

        var osRepo = Assert.Single(repos, static r => string.Equals(r.Id, "os", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFullPath(checkoutRoot), osRepo.LocalPath);

        var resolved = workTree.ResolveRepository(RepositoryId.Parse("os"));
        Assert.Equal("os", resolved.Id);
        Assert.Equal(Path.GetFullPath(checkoutRoot), resolved.LocalPath);
    }

    [Fact]
    public void FindCheckoutRoot_WalksUpToGitDirectoryAndWorktreeGitFile()
    {
        var repoRoot = Path.Combine(_tempHome, "repo");
        var nestedDir = Path.Combine(repoRoot, "lib", "AppHost");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        Directory.CreateDirectory(nestedDir);

        Assert.Equal(Path.GetFullPath(repoRoot), OperatingSystemHosting.FindCheckoutRoot(nestedDir));

        var worktreeRoot = Path.Combine(_tempHome, "worktree");
        var worktreeNested = Path.Combine(worktreeRoot, "a", "b");
        Directory.CreateDirectory(worktreeNested);
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), "gitdir: ../repo/.git/worktrees/wt1");

        Assert.Equal(Path.GetFullPath(worktreeRoot), OperatingSystemHosting.FindCheckoutRoot(worktreeNested));

        var notAGitRepo = Path.Combine(_tempHome, "plain");
        Directory.CreateDirectory(notAGitRepo);
        Assert.Null(OperatingSystemHosting.FindCheckoutRoot(notAGitRepo));
    }

    [Fact]
    public async Task LocalTopology_DoesNotOverwriteAlreadyConfiguredOsRepository()
    {
        var env = await ResolveKernelEnvironmentAsync("local", ("IntoChatOS:Repositories:os", @"C:\custom\os-repo"));

        Assert.DoesNotContain("IntoChatOS__Repositories__os", env.Keys);
    }

    [Fact]
    public async Task CloudTopology_DoesNotProjectLocalCheckout()
    {
        var env = await ResolveKernelEnvironmentAsync("cloud");

        Assert.DoesNotContain("IntoChatOS__Repositories__os", env.Keys);
    }

    private static async Task<Dictionary<string, object>> ResolveKernelEnvironmentAsync(
        string topology,
        params (string Key, string Value)[] settings)
    {
        var args = new List<string>
        {
            $"--IntoChatOS:Topology={topology}",
            "--IntoChatOS:Shell:Enabled=false",
        };
        foreach (var (key, value) in settings)
        {
            args.Add($"--{key}={value}");
        }

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [.. args],
            DisableDashboard = true,
        });

        var kernelPath = DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel");
        var silos = builder.AddIntoChatOS("Modules").AddKernel(kernelPath);
        var kernel = silos[0].Resource;

        var env = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, kernel, env, TestContext.Current.CancellationToken);
        foreach (var callback in kernel.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return env;
    }
}
