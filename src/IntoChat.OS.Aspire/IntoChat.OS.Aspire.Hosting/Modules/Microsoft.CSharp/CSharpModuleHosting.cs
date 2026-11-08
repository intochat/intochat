using System.Diagnostics;
using System.Xml.Linq;
using DigitalBrain.Modules.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.Microsoft.CSharp;

// Declares the always-on csharp-sandbox container every brain's session runs in. Its independent
// health endpoint must be ready before the OS starts bootstrapping persistent app behaviors.
public sealed class CSharpModuleHosting : ModuleHosting<IntoChat.OS.Modules.Microsoft.CSharp.CSharpModule>
{
    public const string ResourceName = "csharp-sandbox";
    private const string Dockerfile = "src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/Dockerfile";

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        if (brain.ApplicationBuilder.Configuration[$"{ModuleConfiguration.ModuleSection("csharp")}:Enabled"] == "false") { return; }
        // The image builds from the repository (or its sibling modules repository); a host composed without one has no container.
        if (RepositoryRoot.Find() is not { } repository) { return; }
        var contextPath = repository;
        if (!File.Exists(Path.Combine(contextPath, Dockerfile)))
        {
            var modulesRepo = Path.GetFullPath(Path.Combine(repository, "..", "modules"));
            if (File.Exists(Path.Combine(modulesRepo, Dockerfile)))
            {
                contextPath = modulesRepo;
            }
            else
            {
                return;
            }
        }
        EnsurePublished(contextPath);
        var sandbox = brain.ApplicationBuilder.AddDockerfile(ResourceName, contextPath, dockerfilePath: Dockerfile)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health")
            .WithOtlpExporter()
            .WithAnnotation(new TelemetryExportBarrierAnnotation())
            .WithVolume("intochat-os-csharp-cache", "/cache")
            // Linux engines only resolve host.docker.internal, which sessions dial back to the brain on, with this mapping.
            .WithContainerRuntimeArgs("--add-host", "host.docker.internal:host-gateway")
            .WithParentRelationship(brain.Resource);
        if (ResolveGitHubPackagesToken() is { Length: > 0 } token)
        {
            sandbox.WithBuildArg("NUGET_TOKEN", token);
        }
        brain.AddProjection(new SandboxUrlProjection(sandbox));
    }

    private static void EnsurePublished(string contextPath)
    {
        var projectPath = Path.Combine(contextPath, "src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox.csproj");
        var publishDir = Path.Combine(contextPath, "src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/publish");
        var targetDll = Path.Combine(publishDir, "IntoChat.OS.Modules.Microsoft.CSharp.Sandbox.dll");
        if (!File.Exists(projectPath)) { return; }

        if (File.Exists(targetDll) && File.GetLastWriteTimeUtc(targetDll) >= File.GetLastWriteTimeUtc(projectPath))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("dotnet", $"publish \"{projectPath}\" -c Release -r linux-x64 --no-self-contained -o \"{publishDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(60000);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // If host publish fails, container build failure will surface diagnostics.
            Debug.WriteLine(error);
        }
    }

    private static string? ResolveGitHubPackagesToken()
    {
        foreach (var variable in new[] { "NUGET_AUTH_TOKEN", "NUGET_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } envToken)
            {
                return envToken;
            }
        }

        string[] configs =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config"),
        ];
        foreach (var config in configs.Where(File.Exists))
        {
            var password = XDocument.Load(config).Root?
                .Element("packageSourceCredentials")?
                .Element("github")?
                .Elements("add")
                .FirstOrDefault(element => string.Equals(element.Attribute("key")?.Value, "ClearTextPassword", StringComparison.OrdinalIgnoreCase))?
                .Attribute("value")?.Value;
            if (!string.IsNullOrWhiteSpace(password))
            {
                return password;
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                if (process.ExitCode == 0 && output.Length > 0)
                {
                    return output;
                }
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private sealed class SandboxUrlProjection(IResourceBuilder<ContainerResource> sandbox) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(
                ModuleConfiguration.EnvironmentName(CSharpOptions.SectionName, nameof(CSharpOptions.SandboxUrl)), sandbox.GetEndpoint("http"))
                .WithAnnotation(new WaitAnnotation(sandbox.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
    }
}
