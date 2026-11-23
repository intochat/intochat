using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Hosts the hardened <c>sandbox</c> container running <c>IntoChat.OS.Sandbox</c> with a read-only rootfs,
/// dropped Linux capabilities, and zero host bind mounts. The image is built automatically via .NET SDK
/// Container Publishing (<c>dotnet publish /t:PublishContainer</c>) before container startup so MSBuild
/// resolves the full transitive project graph with zero Dockerfile COPY drift.
/// </summary>
public sealed class SandboxModuleHosting : IIntoChatOSModuleHosting
{
    public const string ResourceName = "sandbox";
    public const string ModuleName = "sandbox";
    internal const string ImageName = "intochat-os-sandbox";
    internal const string ImageTag = "local";
    private const string SandboxProject = "src/IntoChat.OS.Sandbox/IntoChat.OS.Sandbox.csproj";

    public ModuleId Module { get; } = new(ModuleName);

    public void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        if (brain.ApplicationBuilder.Configuration[$"{ModuleConfiguration.ModuleSection(ModuleName)}:Enabled"] == "false")
        {
            return;
        }

        if (IntoChat.OS.Modules.ArtCraft.Aspire.Hosting.RepositoryRoot.Find() is not { } repoRoot)
        {
            return;
        }

        var projectPath = Path.Combine(repoRoot, SandboxProject);
        if (!File.Exists(projectPath))
        {
            return;
        }

        var enrollKey = brain.ApplicationBuilder.Configuration[$"{ModuleConfiguration.ModuleOptionsSection(ModuleName)}:EnrollKey"];
        if (string.IsNullOrWhiteSpace(enrollKey))
        {
            enrollKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        }

#pragma warning disable ASPIRECERTIFICATES001
        var sandbox = brain.ApplicationBuilder
            .AddContainer(ResourceName, ImageName, ImageTag)
            .WithImagePullPolicy(ImagePullPolicy.Never)
            .WithDeveloperCertificateTrust(false)
            .WithCertificateTrustScope(CertificateTrustScope.None)
            .WithoutHttpsCertificate()
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("HOME", "/tmp")
            .WithEnvironment("SANDBOX_ENROLL_KEY", enrollKey)
            // Hardened isolation: read-only rootfs, no Linux caps, no host bind mounts (-v), writable /tmp tmpfs only.
            .WithContainerRuntimeArgs(
                "--read-only",
                "--cap-drop=ALL",
                "--security-opt=no-new-privileges:true",
                "--memory=512m",
                "--cpus=1.0",
                "--tmpfs",
                "/tmp:rw,noexec,nosuid,size=64m",
                "--add-host",
                "host.docker.internal:host-gateway")
            .WithParentRelationship(brain.GetOrAddModuleNode(ModuleName));
#pragma warning restore ASPIRECERTIFICATES001

        if (!brain.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            brain.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
                sandbox.Resource,
                (_, ct) => PublishContainerImageAsync(repoRoot, projectPath, ct));
        }

        brain.AddProjection(new SandboxKernelProjection(sandbox, enrollKey));
    }

    private static async Task PublishContainerImageAsync(string repoRoot, string projectPath, CancellationToken ct)
    {
        var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("-r");
        start.ArgumentList.Add(rid);
        start.ArgumentList.Add("--no-self-contained");
        start.ArgumentList.Add("/t:PublishContainer");
        start.ArgumentList.Add("-p:CodeGraphRefresh=false");
        start.ArgumentList.Add("-p:DebugType=none");
        start.ArgumentList.Add("--nologo");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start 'dotnet publish /t:PublishContainer' for IntoChat.OS.Sandbox.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Building sandbox container image '{ImageName}:{ImageTag}' failed (exit {process.ExitCode}).\n{stderr}\n{stdout}");
        }
    }

    private sealed class SandboxKernelProjection(IResourceBuilder<ContainerResource> sandbox, string enrollKey)
        : IntoChatOSModuleProjection
    {
        private bool _boundKernelUrl;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            var optionsPrefix = ModuleConfiguration.ModuleOptionsSection(new ModuleId(ModuleName));
            builder
                .WithEnvironment(ModuleConfiguration.EnvironmentName(optionsPrefix, "Url"), sandbox.GetEndpoint("http"))
                .WithEnvironment(ModuleConfiguration.EnvironmentName(optionsPrefix, "EnrollKey"), enrollKey);

            if (!_boundKernelUrl && builder.Resource is IResourceWithEndpoints kernel)
            {
                _boundKernelUrl = true;
                var kernelHttp = kernel.GetEndpoint("http");
                var instance = sandbox.ApplicationBuilder.CreateResourceBuilder(kernelHttp.Resource);
                sandbox.WithReference(kernelHttp)
                    .WaitFor(instance)
                    .WithEnvironment("KERNEL_URL", kernelHttp);
            }
        }
    }
}
