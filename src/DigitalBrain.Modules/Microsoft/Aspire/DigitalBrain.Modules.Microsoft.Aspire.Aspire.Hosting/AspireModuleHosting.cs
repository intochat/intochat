using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Aspire.Cluster;
using global::Aspire.Hosting;
using global::Aspire.Hosting.ApplicationModel;
using global::Aspire.Hosting.Yarp;

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

    // The OS silo and the MCP client each run as two named instances, {role}-a and {role}-b, on dynamic ports
    // (Aspire 13.6 replicates only project resources, and a live build is an executable). A YARP front owns the
    // role's fixed host port and balances across them; its active health check takes an instance out while
    // it restarts, so the port stays served during a roll.
    public static readonly string[] Instances = ["a", "b"];
    // ServiceDefaults' readiness: /health plus the drain of a stopping instance.
    public const string HealthPath = "/ready";

    public static string[] InstanceNames(string role) => [.. Instances.Select(instance => $"{role}-{instance}")];

    // An instance listens on dynamic ports only: a project's launch profile pins its applicationUrl ports, which
    // two instances of the same project would share.
    public static IResourceBuilder<T> OnDynamicPorts<T>(IResourceBuilder<T> instance, string? env = null) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.Resource.Annotations.OfType<EndpointAnnotation>().Any(endpoint => endpoint.Name == "http"))
        {
            instance.WithHttpEndpoint(name: "http", env: env);
        }
        // Not proxied either: Aspire's proxy keeps accepting while the instance restarts and holds the request,
        // where a closed port is refused at once and the front moves on to the other instance.
        foreach (var endpoint in instance.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.Port = null;
            endpoint.IsProxied = false;
        }
        return instance;
    }

    public static string FrontName(string role) => $"{role}-front";

    public static IResourceBuilder<YarpResource> AddFront(IDistributedApplicationBuilder builder, string role, int port, IEnumerable<EndpointReference> instances)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EndpointReference[] endpoints = [.. instances];
        var front = builder.AddYarp(FrontName(role))
            .WithHostPort(port)
            .WithConfiguration(yarp => yarp.AddRoute("/{**catch-all}", yarp.AddCluster(role, [.. endpoints.Cast<object>()])
                .WithHealthCheckConfig(new global::Yarp.ReverseProxy.Configuration.HealthCheckConfig
                {
                    Active = new global::Yarp.ReverseProxy.Configuration.ActiveHealthCheckConfig { Enabled = true, Path = HealthPath },
                    Passive = new global::Yarp.ReverseProxy.Configuration.PassiveHealthCheckConfig { Enabled = true },
                })));
        // The destinations resolve through service discovery (http://{instance}); the front starts after them. A
        // stopping instance drains (ServiceDefaults): /ready fails and it keeps serving until the probe has
        // taken it out of the cluster.
        foreach (var endpoint in endpoints)
        {
            var instance = builder.CreateResourceBuilder(endpoint.Resource);
            front.WithReference(endpoint).WaitForStart(instance);
            if (endpoint.Resource is IResourceWithEnvironment)
            {
                builder.CreateResourceBuilder((IResourceWithEnvironment)endpoint.Resource).WithEnvironment(DrainDelayVariable, DrainDelay.ToString("c"));
            }
        }
        // Aspire 13.6 writes a TimeSpan of the cluster config as its fields (INTERVAL__TICKS, ...), which YARP's
        // binder does not read; the probe cadence is set as the section's own value instead.
        var health = $"REVERSEPROXY__CLUSTERS__cluster_{role}__HEALTHCHECK__";
        return front
            .WithEnvironment(health + "ACTIVE__INTERVAL", ProbeInterval.ToString("c"))
            .WithEnvironment(health + "ACTIVE__TIMEOUT", ProbeTimeout.ToString("c"))
            .WithEnvironment(health + "PASSIVE__REACTIVATIONPERIOD", Reactivation.ToString("c"));
    }

    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    // A busy silo answers its probe in more than a second; a timed-out probe counts as a failure.
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Reactivation = TimeSpan.FromSeconds(5);
    // YARP marks a destination unhealthy after two failed probes (ConsecutiveFailures), so draining covers five.
    public static readonly TimeSpan DrainDelay = ProbeInterval * 5;
    public static string DrainDelayVariable => DigitalBrainConfiguration.EnvironmentName(DigitalBrainHostingOptions.DrainDelayKey);

    public const string OtlpHttpEndpoint = Candidates.DockerCandidateHost.OtlpHttpEndpoint;
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
