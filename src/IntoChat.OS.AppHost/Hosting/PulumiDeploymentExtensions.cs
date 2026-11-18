#pragma warning disable ASPIREPIPELINES001
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using IntoChat.OS.LocalSetup;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Registers custom Aspire 17.1 pipeline steps (<c>aspire do &lt;step&gt;</c>) for release packaging,
/// local reset, and Pulumi Infrastructure-as-Code stack synthesis &amp; operations.
/// </summary>
public static class PulumiDeploymentExtensions
{
    public const string PackageReleaseStepName = "package-release";
    public const string PackageDryRunStepName = "package-dry-run";
    public const string PackageMsixStepName = "package-msix";
    public const string ResetStepName = "reset";
    public const string PulumiStepName = "pulumi";
    public const string PulumiPreviewStepName = "pulumi-preview";
    public const string PulumiRefreshStepName = "pulumi-refresh";
    public const string PulumiDestroyStepName = "pulumi-destroy";

    public static IDistributedApplicationBuilder AddIntoChatOSPipelines(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddReleasePackagingPipeline();
        builder.AddPulumiDeployment();
        return builder;
    }

    public static IDistributedApplicationBuilder AddReleasePackagingPipeline(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Pipeline.AddStep(PackageDryRunStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Validating release packaging inputs (Native AOT Desktop + Kernel + UnitHost + Flutter)", context.CancellationToken);
            var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
            var rid = builder.Configuration["Release:Rid"];
            var exitCode = await ReleasePackager.RunAsync(new ReleasePackageOptions
            {
                RepoRoot = repoRoot,
                Rid = rid,
                DryRun = true,
            }, Console.Out, Console.Error, context.CancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Release packaging dry-run failed with exit code {exitCode}.");
            }
            await task.SucceedAsync("All release packaging inputs verified.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(PackageReleaseStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Packaging IntoChat OS release bundle (Native AOT)", context.CancellationToken);
            var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
            var rid = builder.Configuration["Release:Rid"];
            var outputDir = builder.Configuration["Release:OutputDir"];
            var skipFlutter = builder.Configuration.GetValue<bool>("Release:SkipFlutter");
            var exitCode = await ReleasePackager.RunAsync(new ReleasePackageOptions
            {
                RepoRoot = repoRoot,
                Rid = rid,
                OutputDir = outputDir,
                SkipFlutter = skipFlutter,
            }, Console.Out, Console.Error, context.CancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Release packaging failed with exit code {exitCode}.");
            }
            await task.SucceedAsync("Packaged release bundle and updated SHA256SUMS, latest.json, and package manifests.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(PackageMsixStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Packaging IntoChat OS Windows MSIX installer", context.CancellationToken);
            var exitCode = await MsixPackager.RunAsync([]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"MSIX packaging failed with exit code {exitCode}.");
            }
            await task.SucceedAsync("Packaged Windows MSIX installer.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(ResetStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Resetting local IntoChat OS checkout state", context.CancellationToken);
            var exitCode = await LocalReset.RunFromAppHostAsync(builder, ["--yes"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Local reset failed with exit code {exitCode}.");
            }
            await task.SucceedAsync("Local checkout state reset.", context.CancellationToken);
        });

        return builder;
    }

    public static IDistributedApplicationBuilder AddPulumiDeployment(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Pipeline.AddStep(PulumiPreviewStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Synthesizing Pulumi stack and running pulumi preview", context.CancellationToken);
            await RunPulumiStepAsync(builder, ["preview"], context.CancellationToken);
            await task.SucceedAsync("Pulumi preview completed.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(PulumiStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Synthesizing Pulumi stack and running pulumi up --yes", context.CancellationToken);
            await RunPulumiStepAsync(builder, ["up", "--yes"], context.CancellationToken);
            await task.SucceedAsync("Pulumi up completed.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(PulumiRefreshStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Synthesizing Pulumi stack and refreshing live Azure state", context.CancellationToken);
            await RunPulumiStepAsync(builder, ["refresh", "--yes"], context.CancellationToken);
            await task.SucceedAsync("Pulumi refresh completed.", context.CancellationToken);
        });

        builder.Pipeline.AddStep(PulumiDestroyStepName, async context =>
        {
            await using var task = await context.ReportingStep.CreateTaskAsync("Running pulumi destroy --yes", context.CancellationToken);
            await RunPulumiStepAsync(builder, ["destroy", "--yes"], context.CancellationToken);
            await task.SucceedAsync("Pulumi destroy completed.", context.CancellationToken);
        });

        return builder;
    }

    public static async Task<int> RunPulumiCliAsync(IDistributedApplicationBuilder builder, string subcommand, string[] extraArgs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(extraArgs);

        var env = builder.Configuration["Pulumi:Stack"] ?? "test";
        string? configPath = builder.Configuration["Pulumi:Config"];
        var synthOnly = false;
        var pulumiArgs = new List<string>();

        for (var i = 0; i < extraArgs.Length; i++)
        {
            switch (extraArgs[i])
            {
                case "--stack" or "-s" or "--env" when i + 1 < extraArgs.Length:
                    env = extraArgs[++i];
                    break;
                case "--config" when i + 1 < extraArgs.Length:
                    configPath = extraArgs[++i];
                    break;
                case "--synth-only" or "--emit-only" or "--dry-run":
                    synthOnly = true;
                    break;
                default:
                    pulumiArgs.Add(extraArgs[i]);
                    break;
            }
        }

        var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
        configPath ??= Path.Combine(repoRoot, "deploy", $"{env}.json");
        var pulumiWorkDir = Path.Combine(repoRoot, "artifacts", "pulumi", env);

        await SynthesizePulumiWorkspaceAsync(repoRoot, env, configPath, pulumiWorkDir, cancellationToken);
        if (synthOnly)
        {
            return 0;
        }

        string[] verbArgs = subcommand switch
        {
            PulumiPreviewStepName => ["preview", "--stack", env, .. pulumiArgs],
            PulumiRefreshStepName => ["refresh", "--yes", "--stack", env, .. pulumiArgs],
            PulumiDestroyStepName => ["destroy", "--yes", "--stack", env, .. pulumiArgs],
            _ => ["up", "--yes", "--stack", env, .. pulumiArgs],
        };

        await EnsurePulumiStackSelectedAsync(pulumiWorkDir, env, cancellationToken);
        return await RunProcessAsync("pulumi", verbArgs, pulumiWorkDir, cancellationToken);
    }

    private static async Task RunPulumiStepAsync(IDistributedApplicationBuilder builder, string[] verbArgs, CancellationToken cancellationToken)
    {
        var env = builder.Configuration["Pulumi:Stack"] ?? "test";
        var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
        var configPath = builder.Configuration["Pulumi:Config"] ?? Path.Combine(repoRoot, "deploy", $"{env}.json");
        var pulumiWorkDir = Path.Combine(repoRoot, "artifacts", "pulumi", env);

        await SynthesizePulumiWorkspaceAsync(repoRoot, env, configPath, pulumiWorkDir, cancellationToken);
        if (builder.Configuration.GetValue<bool>("Pulumi:SynthOnly"))
        {
            return;
        }

        await EnsurePulumiStackSelectedAsync(pulumiWorkDir, env, cancellationToken);
        var exitCode = await RunProcessAsync("pulumi", [.. verbArgs, "--stack", env], pulumiWorkDir, cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"pulumi {string.Join(' ', verbArgs)} --stack {env} failed with exit code {exitCode}.");
        }
    }

    private static async Task SynthesizePulumiWorkspaceAsync(string repoRoot, string env, string configPath, string pulumiWorkDir, CancellationToken cancellationToken)
    {
        var kernelProject = Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj");
        var exitCode = await RunProcessAsync(
            "dotnet",
            ["run", "--project", kernelProject, "-c", "Release", "--", "pulumi", "--env", env, "--config", configPath, "--pulumi-out", pulumiWorkDir, "--dry-run"],
            repoRoot,
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to synthesize Pulumi workspace for '{env}' from '{configPath}'.");
        }
    }

    private static async Task EnsurePulumiStackSelectedAsync(string pulumiWorkDir, string stackName, CancellationToken cancellationToken)
    {
        var stateDir = Path.Combine(pulumiWorkDir, ".pulumi-state");
        Directory.CreateDirectory(stateDir);
        var loginExit = await RunProcessAsync("pulumi", ["login", $"file://{stateDir.Replace('\\', '/')}"], pulumiWorkDir, cancellationToken);
        if (loginExit != 0)
        {
            throw new InvalidOperationException("Failed to initialize local Pulumi state backend.");
        }
        _ = await RunProcessAsync("pulumi", ["stack", "select", "--create", stackName], pulumiWorkDir, cancellationToken);
    }

    private static async Task<int> RunProcessAsync(string fileName, string[] args, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        psi.Environment["PULUMI_CONFIG_PASSPHRASE"] ??= "";
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
