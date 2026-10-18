using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;

namespace IntoChat.OS.Microsoft.CSharp;

// Declares the always-on csharp-sandbox container every brain's session runs in. Its independent
// health endpoint must be ready before the OS starts bootstrapping persistent app behaviors.
public sealed class CSharpModuleHosting : ModuleHosting<IntoChat.OS.Microsoft.CSharp.CSharpModule>
{
    public const string ResourceName = "csharp-sandbox";
    private const string Dockerfile = "src/IntoChat.OS.Modules/Microsoft/CSharp/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/Dockerfile";

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        // The image builds from the repository; a host composed without one (the session-pool path) has no container.
        if (RepositoryRoot.Find() is not { } repository) { return; }
        var sandbox = brain.ApplicationBuilder.AddDockerfile(ResourceName, repository, dockerfilePath: Dockerfile)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithOtlpExporter()
            .WithAnnotation(new TelemetryExportBarrierAnnotation())
            .WithVolume("intochat-os-csharp-cache", "/cache")
            // Linux engines only resolve host.docker.internal, which sessions dial back to the brain on, with this mapping.
            .WithContainerRuntimeArgs("--add-host", "host.docker.internal:host-gateway")
            .WithParentRelationship(brain.Resource);
        brain.AddProjection(new SandboxUrlProjection(sandbox));
    }

    private sealed class SandboxUrlProjection(IResourceBuilder<ContainerResource> sandbox) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(
                IntoChatOSConfiguration.EnvironmentName(CSharpDeploymentSettings.SectionName, nameof(CSharpDeploymentSettings.SandboxUrl)), sandbox.GetEndpoint("http"))
                .WithAnnotation(new WaitAnnotation(sandbox.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
    }
}
