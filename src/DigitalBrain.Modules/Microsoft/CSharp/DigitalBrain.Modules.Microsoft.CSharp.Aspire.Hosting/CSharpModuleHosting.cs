using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Configuration;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Microsoft.CSharp;

// Declares the sandbox stopped: the brain starts it through IAspire when the first script runs.
public sealed class CSharpModuleHosting : ModuleHosting<DigitalBrain.Microsoft.CSharp.CSharpModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = HostingModuleOptions.GetModuleOptions<CSharpOptions>(brain.GetModuleConfiguration("csharp"), "csharp");
        // The same fallback the silo uses, so composing the module in a repository always declares
        // the sandbox; only a host without any repository (the session-pool path) has none.
        if (string.IsNullOrWhiteSpace(options.SourceRoot)) { options.SourceRoot = RepositoryRoot.Find(); }
        if (string.IsNullOrWhiteSpace(options.SourceRoot)) { return; }
        var sandboxProject = Path.Combine(options.SourceRoot, CSharpSandbox.SourceProject);
        var sandbox = brain.ApplicationBuilder.AddDockerfile(CSharpSandbox.ResourceName, sandboxProject)
            // Image identity is the docker layer cache's job: the Dockerfile and .dockerignore
            // define the inputs, and an unchanged-source rebuild is a cache hit.
            .WithHttpEndpoint(targetPort: CSharpSandbox.Port, name: "http")
            .WithHttpHealthCheck("/health")
            .WithBindMount(options.SourceRoot, CSharpSandbox.SourceMount, isReadOnly: true)
            .WithVolume("digitalbrain-csharp-nuget", "/root/.nuget/packages")
            // Linux engines only resolve host.docker.internal, which the script's gateway relay dials, with this mapping.
            .WithContainerRuntimeArgs("--add-host", "host.docker.internal:host-gateway")
            .WithParentRelationship(brain.Resource)
            .WithExplicitStart();
        // The endpoint proxy listens from allocation, long before the container's first start,
        // so the silo can always probe the sandbox directly - the resource-state bridge is an
        // optimization, not the only truth (its watch can die mid-session).
        brain.AddProjection(new SandboxProbeProjection(sandbox.GetEndpoint("http")));
        brain.AddProjection(new SourceRootProjection(options.SourceRoot));
    }

    // The silo reads the same root the sandbox mounts; the module no longer searches for it.
    private sealed class SourceRootProjection(string sourceRoot) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(
                DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.ModuleOptionsSection("csharp") + ":" + nameof(CSharpOptions.SourceRoot)), sourceRoot);
    }

    private sealed class SandboxProbeProjection(EndpointReference endpoint) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(
                DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.EnvironmentName(CSharpDeploymentSettings.SectionName, nameof(CSharpDeploymentSettings.SandboxProbeUrl)), endpoint);
    }
}
