using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Microsoft.CSharp;

// Declares the always-on csharp-sandbox container every brain's session runs in. The OS never waits on
// it: brains start their sessions after readiness, and a sandbox that is not up yet is retried by each
// brain runtime's reminder.
public sealed class CSharpModuleHosting : ModuleHosting<DigitalBrain.Microsoft.CSharp.CSharpModule>
{
    public const string ResourceName = "csharp-sandbox";
    private const string Dockerfile = "src/DigitalBrain.Modules/Microsoft/CSharp/DigitalBrain.Modules.Microsoft.CSharp.Sandbox/Dockerfile";

    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        // The image builds from the repository; a host composed without one (the session-pool path) has no container.
        if (RepositoryRoot.Find() is not { } repository) { return; }
        var sandbox = brain.ApplicationBuilder.AddDockerfile(ResourceName, repository, dockerfilePath: Dockerfile)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithOtlpExporter()
            .WithVolume("digitalbrain-csharp-cache", "/cache")
            // Linux engines only resolve host.docker.internal, which sessions dial back to the brain on, with this mapping.
            .WithContainerRuntimeArgs("--add-host", "host.docker.internal:host-gateway")
            .WithParentRelationship(brain.Resource);
        brain.AddProjection(new SandboxUrlProjection(sandbox.GetEndpoint("http")));
    }

    private sealed class SandboxUrlProjection(EndpointReference endpoint) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(
                DigitalBrainConfiguration.EnvironmentName(CSharpDeploymentSettings.SectionName, nameof(CSharpDeploymentSettings.SandboxUrl)), endpoint);
    }
}
