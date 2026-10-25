using IntoChat.OS.Modules.Microsoft.Aspire;
using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Platform.Integrations;
using IntoChat.OS.ServiceDefaults;
using global::Aspire.Hosting;
using global::Aspire.Hosting.ApplicationModel;
using global::Aspire.Hosting.Yarp;
using global::Aspire.Hosting.Yarp.Transforms;

namespace IntoChat.OS.Modules.Microsoft.Aspire;

// Candidates work in {repository}/.intochat-os (gitignored): worktrees, builds and the candidate Dockerfile.
public sealed class AspireModuleHosting : ModuleHosting<IntoChat.OS.Modules.Microsoft.Aspire.AspireModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        brain.AddProjection(new Workdir(WorkdirOf(brain.ApplicationBuilder)));
        brain.AddProjection(new Fronts(brain.ApplicationBuilder));
        brain.AddProjection(new CandidateSecrets());
        // Candidates are containers: they reach the dashboard's plain-HTTP OTLP endpoint (launchSettings), not the
        // TLS one, whose dev certificate names localhost and is not trusted inside the container.
        if (HostConfiguration.Variable(brain.ApplicationBuilder.Configuration, OtlpHttpEndpoint) is { Length: > 0 } otlp)
        {
            brain.AddProjection(new Variable(OtlpHttpEndpoint, otlp));
        }
    }

    public static string WorkdirOf(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Path.Combine(Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")), ".intochat-os");
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

    // One runtime and one MCP host. Fronts preserve URLs across their restarts.
    public static readonly string[] Instances = ["a"];
    public const string HealthPath = HealthProbes.ReadyPath;

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
        // The single runtime needs a stable endpoint while its process restarts. Aspire owns the
        // proxy listener; the front does not retain a container-to-host mapping to a dead listener.
        foreach (var endpoint in instance.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.Port = null;
            endpoint.IsProxied = true;
        }
        return instance;
    }

    public static string FrontName(string role) => $"{role}-front";

    // Three routes: everything to the role's instances; the front's own /ready, answered by whichever instance the
    // front routes to (its /alive, so a draining instance's 503 does not leak through); and one route per instance,
    // /_instances/{instance}/ready, through a cluster of that instance alone with the same health check. A roll
    // reads that route to see the instance the way the front does: in (200) or out.
    public static IResourceBuilder<YarpResource> AddFront(IDistributedApplicationBuilder builder, string role, int port, IEnumerable<EndpointReference> instances)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EndpointReference[] endpoints = [.. instances];
        var front = builder.AddYarp(FrontName(role))
            .WithHostPort(port)
            .WithConfiguration(yarp =>
            {
                var all = Probed(yarp.AddCluster(role, [.. endpoints.Cast<object>()]));
                yarp.AddRoute("/{**catch-all}", all);
                yarp.AddRoute(HealthProbes.ReadyPath, all).WithTransformPathSet(HealthProbes.AlivePath);
                foreach (var endpoint in endpoints)
                {
                    // Seen live: the front kept routing an instance its probe had marked Unhealthy, its cluster's
                    // only destination (panic fallback). The instance's own route must answer 503 then.
                    yarp.AddRoute(InstancePath(endpoint.Resource.Name), Probed(yarp.AddCluster(endpoint.Resource.Name, endpoint), HealthyOnly))
                        .WithTransformPathSet(HealthProbes.AlivePath);
                }
            });
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
        foreach (var cluster in endpoints.Select(endpoint => endpoint.Resource.Name).Prepend(role))
        {
            var health = $"REVERSEPROXY__CLUSTERS__cluster_{cluster}__HEALTHCHECK__";
            front.WithEnvironment(health + "ACTIVE__INTERVAL", ProbeInterval.ToString("c"))
                .WithEnvironment(health + "ACTIVE__TIMEOUT", ProbeTimeout.ToString("c"))
                .WithEnvironment(health + "PASSIVE__REACTIVATIONPERIOD", Reactivation.ToString("c"));
        }
        return front;
    }

    public static string InstancePath(string instance) => $"/_instances/{instance}{HealthProbes.ReadyPath}";

    private const string HealthyOnly = "HealthyAndUnknown";

    private static global::Aspire.Hosting.Yarp.YarpCluster Probed(global::Aspire.Hosting.Yarp.YarpCluster cluster, string? available = null)
        => cluster.WithHealthCheckConfig(new global::Yarp.ReverseProxy.Configuration.HealthCheckConfig
        {
            AvailableDestinationsPolicy = available,
            Active = new global::Yarp.ReverseProxy.Configuration.ActiveHealthCheckConfig { Enabled = true, Path = HealthPath },
            Passive = new global::Yarp.ReverseProxy.Configuration.PassiveHealthCheckConfig { Enabled = true },
        });

    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    // A busy silo answers its probe in more than a second; a timed-out probe counts as a failure.
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Reactivation = TimeSpan.FromSeconds(5);
    // YARP's ConsecutiveFailures policy takes a destination out after this many failed probes (its default).
    public const int ConsecutiveFailures = 2;
    // The drain outlasts the probes that take the instance out: each comes an interval apart and may run its timeout.
    public static readonly TimeSpan DrainDelay = (ProbeInterval + ProbeTimeout) * ConsecutiveFailures;
    public static string DrainDelayVariable => IntoChatOSConfiguration.EnvironmentName(HealthProbes.DrainDelayKey);

    public const string OtlpHttpEndpoint = global::IntoChat.OS.Modules.Microsoft.Aspire.CandidateEnvironment.OtlpHttpEndpoint;
    public const string AspNetCoreContentRoot = "ASPNETCORE_CONTENTROOT";
    public const string DotnetContentRoot = "DOTNET_CONTENTROOT";
    public const string FeaturesFolder = "features";
    public static string FeaturesKey => IntoChatOSConfiguration.ModuleOptionsSection("apps") + ":Features";

    // The model Integrations a candidate keeps (the AI module's provider ids); every other Integration it boots
    // with is its own.
    public static readonly string[] ModelIntegrations = ["openai", "anthropic", "google", "xai", "openrouter", "typesafe"];

    public static string CandidateModelVariable => IntoChatOSConfiguration.EnvironmentName(AspireModule.Integration.Key(AspireModule.CandidateModelField));
    public static string FeaturesVariable => IntoChatOSConfiguration.EnvironmentName(FeaturesKey);
    public static string IntegrationVariable(string id, string field) => IntoChatOSConfiguration.IntegrationEnvironmentName(id, field);

    private sealed class Workdir(string path) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder,
                IntoChatOSConfiguration.EnvironmentName(AspireModule.Integration.Key("Workdir")), path);
    }

    // Run mode only (candidates are local): reads the silo's own environment as composed so far, so this
    // projection must come after the AI module's and after the brain's grain-state reference (it does: hosting
    // projections apply in registration order, after the references), and hands the aspire Integration the
    // model's values and the live grain-state connection. DockerCandidateHost reads them only from there.
    // Each role's front as the aspire Integration's Fronts ({role: url}), read when the silo starts: the fronts are
    // added after the brain is composed, and their fixed ports are the AppHost's.
    private sealed class Fronts(IDistributedApplicationBuilder app) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, context =>
            {
                var fronts = app.Resources.OfType<YarpResource>()
                    .Where(front => front.Name.EndsWith(FrontSuffix, StringComparison.Ordinal))
                    .ToDictionary(front => front.Name[..^FrontSuffix.Length], front => $"http://localhost:{front.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "http").Port}", StringComparer.Ordinal);
                if (fronts.Count > 0) { context.EnvironmentVariables[FrontsVariable] = JsonSerializer.Serialize(fronts); }
            });
    }

    private const string FrontSuffix = "-front";
    public static string FrontsVariable => IntoChatOSConfiguration.EnvironmentName(AspireModule.Integration.Key(AspireModule.FrontsField));

    // The callback only gathers references: the values (parameters, connection strings of resources not yet
    // allocated) resolve when the orchestrator starts the resource, never while the environment is built.
    private sealed class CandidateSecrets : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, context =>
            {
                if (!context.ExecutionContext.IsRunMode) { return; }
                var prefixes = ModelIntegrations.Select(id => IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.SectionName, id) + "__").ToArray();
                var model = context.EnvironmentVariables
                    .Where(variable => prefixes.Any(prefix => variable.Key.StartsWith(prefix, StringComparison.Ordinal)))
                    .ToArray();
                context.EnvironmentVariables[CandidateModelVariable] = new ModelJson(model);
            });


        // The model Integrations' resolved values as one JSON object, resolved with the resource's other values.
        private sealed class ModelJson(KeyValuePair<string, object>[] variables) : IValueProvider
        {
            public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            {
                var model = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in variables)
                {
                    if (await Resolve(value, cancellationToken) is { Length: > 0 } resolved) { model[name] = resolved; }
                }
                return JsonSerializer.Serialize(model);
            }

            private static async Task<string?> Resolve(object value, CancellationToken ct) => value switch
            {
                string text => text,
                IValueProvider provider => await provider.GetValueAsync(ct),
                _ => value.ToString(),
            };
        }
    }

    private sealed class Variable(string name, string value) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => ResourceBuilderExtensions.WithEnvironment(builder, name, value);
    }
}
