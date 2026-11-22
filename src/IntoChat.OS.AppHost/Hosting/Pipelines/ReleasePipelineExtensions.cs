#pragma warning disable ASPIREPIPELINES001
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using IntoChat.OS.LocalSetup;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Registers Aspire pipeline steps for release packaging (<c>package-dry-run</c>, <c>package-release</c>, <c>package-msix</c>).
/// </summary>
public static class ReleasePipelineExtensions
{
    public const string PackageReleaseStepName = "package-release";
    public const string PackageDryRunStepName = "package-dry-run";
    public const string PackageMsixStepName = "package-msix";

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

        return builder;
    }
}
