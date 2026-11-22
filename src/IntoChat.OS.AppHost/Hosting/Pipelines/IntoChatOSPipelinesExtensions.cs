using Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Registers custom Aspire 17.1 pipeline steps (<c>aspire do &lt;step&gt;</c>) across
/// release packaging, local dev maintenance, and Pulumi Infrastructure-as-Code bounded contexts.
/// </summary>
public static class IntoChatOSPipelinesExtensions
{
    public static IDistributedApplicationBuilder AddIntoChatOSPipelines(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddReleasePackagingPipeline();
        builder.AddLocalDevPipeline();
        builder.AddPulumiDeployment();
        return builder;
    }
}
