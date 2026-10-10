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
        // Candidates are containers: they reach the dashboard's plain-HTTP OTLP endpoint (launchSettings), not the
        // TLS one, whose dev certificate names localhost and is not trusted inside the container.
        if (brain.ApplicationBuilder.Configuration[OtlpHttpEndpoint] is { Length: > 0 } otlp)
        {
            brain.AddProjection(new Variable(OtlpHttpEndpoint, otlp));
        }
    }

    public static string WorkdirOf(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Path.Combine(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")), ".digitalbrain");
    }

    // Once a build is live ({Workdir}/live.json), the resource runs `dotnet {build}/{component}/{assembly}`
    // with the build folder as its content root; null on first boot, and when live.json names a build that
    // is gone, so the AppHost runs from source. Arguments and environment are read when the resource
    // starts, so a restart picks up the build SetLive named.
    public static IResourceBuilder<ExecutableResource>? AddLiveBuild(IDistributedApplicationBuilder builder, string workdir, string name, string component, string assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (LiveBuildFile.Read(workdir) is not { } live) { return null; }
        if (!Directory.Exists(Path.Combine(live.Path, component)))
        {
            Console.Error.WriteLine($"live.json names build {live.Sha}, but {Path.Combine(live.Path, component)} is gone; {name} runs from source.");
            return null;
        }
        string Folder() => Path.Combine((LiveBuildFile.Read(workdir) ?? live).Path, component);
        return builder.AddExecutable(name, "dotnet", workdir)
            .WithArgs(context => context.Args.Add(Path.Combine(Folder(), assembly)))
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["ASPNETCORE_CONTENTROOT"] = Folder();
                context.EnvironmentVariables["DOTNET_CONTENTROOT"] = Folder();
            })
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithOtlpExporter();
    }

    public const string OtlpHttpEndpoint = Candidates.DockerCandidateHost.OtlpHttpEndpoint;

    private sealed class Workdir(string path) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder,
                DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key("Workdir")), path);
    }

    private sealed class Variable(string name, string value) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, name, value);
    }
}
