#pragma warning disable ASPIREPIPELINES001
using Aspire.Hosting;
using Aspire.Hosting.Pipelines;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Registers Aspire pipeline steps for local development maintenance (<c>reset</c>).
/// </summary>
public static class LocalDevPipelineExtensions
{
    public const string ResetStepName = "reset";

    public static IDistributedApplicationBuilder AddLocalDevPipeline(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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
}
