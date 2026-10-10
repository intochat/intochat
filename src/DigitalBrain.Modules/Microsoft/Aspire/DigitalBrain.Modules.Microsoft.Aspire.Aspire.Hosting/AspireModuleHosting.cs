using System.Text.Json;
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
        brain.AddProjection(new CandidateSecrets());
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
                context.EnvironmentVariables[AspNetCoreContentRoot] = Folder();
                context.EnvironmentVariables[DotnetContentRoot] = Folder();
                // The published OS carries the features of its own commit: the live apps are the promoted ones,
                // never the working tree's.
                if (Directory.Exists(Path.Combine(Folder(), FeaturesFolder)))
                {
                    context.EnvironmentVariables[FeaturesVariable] = Path.Combine(Folder(), FeaturesFolder);
                }
            })
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithOtlpExporter();
    }

    // The OS silo and the MCP client each run Replicas processes behind one fixed host port: Aspire's proxy owns
    // the port and balances across replicas, each listening on its own internal port (ASPNETCORE_URLS for a
    // project, `env` for an executable). Orleans silo/gateway ports come per replica from the brain reference.
    // Aspire 13.6 honours the replica count only for project resources: a live-build executable still runs once.
    public const int Replicas = 2;

    public static IResourceBuilder<T> Replicated<T>(IResourceBuilder<T> resource, int port, string? env = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.WithHttpEndpoint(port: port, name: "http", env: env, isProxied: true)
            .WithAnnotation(new ReplicaAnnotation(Replicas), ResourceAnnotationMutationBehavior.Replace);
    }

    public const string OtlpHttpEndpoint =Candidates.DockerCandidateHost.OtlpHttpEndpoint;
    public const string AspNetCoreContentRoot = "ASPNETCORE_CONTENTROOT";
    public const string DotnetContentRoot = "DOTNET_CONTENTROOT";
    public const string FeaturesFolder = "features";
    public static string FeaturesKey => DigitalBrainConfiguration.ModuleOptionsSection("apps") + ":Features";

    // The model Integrations a candidate keeps (the AI module's provider ids); every other Integration it boots
    // with is its own.
    public static readonly string[] ModelIntegrations = ["openai", "anthropic", "google", "xai", "openrouter", "typesafe"];

    public static string CandidateModelVariable => DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key(AspireModule.CandidateModelField));
    public static string LiveGrainStateVariable => DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key(AspireModule.LiveGrainStateField));
    public static string FeaturesVariable => DigitalBrainConfiguration.EnvironmentName(FeaturesKey);
    public static string IntegrationVariable(string id, string field) => DigitalBrainConfiguration.IntegrationEnvironmentName(id, field);

    private sealed class Workdir(string path) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder,
                DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key("Workdir")), path);
    }

    // Run mode only (candidates are local): reads the silo's own environment as composed so far, so this
    // projection must come after the AI module's and after the brain's grain-state reference (it does: hosting
    // projections apply in registration order, after the references), and hands the aspire Integration the
    // model's values and the live grain-state connection. DockerCandidateHost reads them only from there.
    private sealed class CandidateSecrets : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, async context =>
            {
                if (!context.ExecutionContext.IsRunMode) { return; }
                var prefixes = ModelIntegrations.Select(id => DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.SectionName, id) + "__").ToArray();
                var model = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in context.EnvironmentVariables.ToArray())
                {
                    if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) && await Resolve(value, context.CancellationToken) is { Length: > 0 } resolved)
                    { model[name] = resolved; }
                }
                context.EnvironmentVariables[CandidateModelVariable] = JsonSerializer.Serialize(model);
                if (context.EnvironmentVariables.TryGetValue(GrainStateConnection, out var grainState) && await Resolve(grainState, context.CancellationToken) is { Length: > 0 } connection)
                {
                    context.EnvironmentVariables[LiveGrainStateVariable] = connection;
                }
            });

        private static readonly string GrainStateConnection = $"ConnectionStrings__{DigitalBrain.Framework.DigitalBrainNames.GrainState}";

        private static async Task<string?> Resolve(object value, CancellationToken ct) => value switch
        {
            string text => text,
            IValueProvider provider => await provider.GetValueAsync(ct),
            _ => value.ToString(),
        };
    }

    private sealed class Variable(string name, string value) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, name, value);
    }
}
