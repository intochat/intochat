using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Aspire.Cluster;
using global::Aspire.Hosting;
using global::Aspire.Hosting.ApplicationModel;

namespace DigitalBrain.Microsoft.Aspire;

// Candidates work in {repository}/.digitalbrain (gitignored): worktrees, builds and the candidate Dockerfile.
public sealed class AspireModuleHosting : ModuleHosting<DigitalBrain.Microsoft.Aspire.AspireModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        brain.AddProjection(new Workdir(WorkdirOf(brain.ApplicationBuilder)));
    }

    public static string WorkdirOf(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Path.Combine(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")), ".digitalbrain");
    }

    // Once a build is live ({Workdir}/live.json), the resource runs `dotnet {build}/{component}/{assembly}`
    // instead of the project; null on first boot, when the AppHost runs from source. The argument is read
    // when the resource starts, so a restart picks up the build SetLive named.
    public static IResourceBuilder<ExecutableResource>? AddLiveBuild(IDistributedApplicationBuilder builder, string workdir, string name, string component, string assembly)
    {
        if (LiveBuildFile.Read(workdir) is null) { return null; }
        return builder.AddExecutable(name, "dotnet", workdir)
            .WithArgs(context => context.Args.Add(Path.Combine(LiveBuildFile.Read(workdir)!.Path, component, assembly)))
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithOtlpExporter();
    }

    private sealed class Workdir(string path) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder,
                DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key("Workdir")), path);
    }
}
