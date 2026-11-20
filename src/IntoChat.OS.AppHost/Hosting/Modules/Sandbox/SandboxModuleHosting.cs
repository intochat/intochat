using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Log;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

/// <summary>
/// Hosts the hardened <c>sandbox</c> container running <c>IntoChat.OS.Sandbox</c> with a read-only rootfs,
/// dropped Linux capabilities, and zero host bind mounts. Enabled by default in local and cloud deployments.
/// </summary>
public sealed class SandboxModuleHosting : IIntoChatOSModuleHosting
{
    public const string ResourceName = "sandbox";
    public const string ModuleName = "sandbox";
    private const string Dockerfile = "src/IntoChat.OS.Sandbox/Dockerfile";

    public ModuleId Module { get; } = new(ModuleName);

    public void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        if (brain.ApplicationBuilder.Configuration[$"{ModuleConfiguration.ModuleSection(ModuleName)}:Enabled"] == "false")
        {
            return;
        }

        if (IntoChat.OS.Modules.ArtCraft.Aspire.Hosting.RepositoryRoot.Find() is not { } contextPath)
        {
            return;
        }

        var dockerfileFull = Path.Combine(contextPath, Dockerfile);
        if (!File.Exists(dockerfileFull))
        {
            return;
        }

        var enrollKey = brain.ApplicationBuilder.Configuration[$"{ModuleConfiguration.ModuleOptionsSection(ModuleName)}:EnrollKey"];
        if (string.IsNullOrWhiteSpace(enrollKey))
        {
            enrollKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        }

        var (buildContextPath, localFeedStaged, cleanupPath) = PrepareBuildContext(contextPath);

#pragma warning disable ASPIRECERTIFICATES001
        var sandbox = brain.ApplicationBuilder
            .AddDockerfile(ResourceName, buildContextPath, dockerfilePath: Dockerfile)
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

        if (localFeedStaged)
        {
            sandbox.WithBuildArg("LOCAL_FEED_SUBDIR", "local-feed");
        }

        if (cleanupPath is { Length: > 0 })
        {
            RegisterStagedContextCleanup(brain.ApplicationBuilder, cleanupPath);
        }

        if (ResolveGitHubPackagesToken(contextPath) is { Length: > 0 } token)
        {
            sandbox.WithBuildArg("NUGET_TOKEN", token);
        }

        brain.AddProjection(new SandboxKernelProjection(sandbox, enrollKey));
    }

    private static (string ContextPath, bool LocalFeedStaged, string? CleanupPath) PrepareBuildContext(string repoRoot)
    {
        var nugetConfigPath = Path.Combine(repoRoot, "nuget.config");
        if (!File.Exists(nugetConfigPath))
        {
            return (repoRoot, false, null);
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(nugetConfigPath);
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException)
        {
            OsConsoleLog.For(typeof(SandboxModuleHosting)).LogSwallowed(error, "parsing nuget.config for local NuGet feeds");
            return (repoRoot, false, null);
        }

        var packageSources = doc.Root?.Element("packageSources")?.Elements("add").ToArray() ?? [];
        var localFeeds = new List<(XElement Element, string FullPath)>();
        foreach (var source in packageSources)
        {
            var rawValue = source.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(rawValue)
                || rawValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || rawValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = Path.GetFullPath(rawValue, repoRoot);
            if (Directory.Exists(resolved))
            {
                localFeeds.Add((source, resolved));
            }
        }

        if (localFeeds.Count == 0)
        {
            return (repoRoot, false, null);
        }

        var stagedRoot = Path.Combine(Path.GetTempPath(), $"intochat-os-sandbox-ctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagedRoot);

        string[] rootFiles =
        [
            "Directory.Build.props",
            "Directory.Build.targets",
            "Directory.Packages.props",
            "Intochat.OS.slnx",
            "global.json",
            "VERSION",
            ".editorconfig",
            ".dockerignore",
        ];
        foreach (var file in rootFiles)
        {
            var src = Path.Combine(repoRoot, file);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(stagedRoot, file), overwrite: true);
            }
        }

        string[] sourceDirs =
        [
            Path.Combine("src", "IntoChat.OS"),
            Path.Combine("src", "IntoChat.OS.UI"),
            Path.Combine("src", "IntoChat.OS.Sandbox"),
            Path.Combine("framework", "src"),
        ];
        foreach (var relDir in sourceDirs)
        {
            var srcDir = Path.Combine(repoRoot, relDir);
            if (Directory.Exists(srcDir))
            {
                CopyDirectoryFiltered(srcDir, Path.Combine(stagedRoot, relDir));
            }
        }

        var stagedFeedDir = Path.Combine(stagedRoot, "local-feed");
        Directory.CreateDirectory(stagedFeedDir);
        foreach (var (element, feedDir) in localFeeds)
        {
            foreach (var nupkg in Directory.EnumerateFiles(feedDir, "*.nupkg", SearchOption.TopDirectoryOnly))
            {
                File.Copy(nupkg, Path.Combine(stagedFeedDir, Path.GetFileName(nupkg)), overwrite: true);
            }

            element.SetAttributeValue("value", "/src/local-feed");
        }

        doc.Save(Path.Combine(stagedRoot, "nuget.config"));
        return (stagedRoot, true, stagedRoot);
    }

    private static void CopyDirectoryFiltered(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectoryFiltered(dir, Path.Combine(destinationDir, name));
        }
    }

    private static void RegisterStagedContextCleanup(IDistributedApplicationBuilder builder, string stagedRoot)
    {
        void Cleanup()
        {
            try
            {
                if (Directory.Exists(stagedRoot))
                {
                    Directory.Delete(stagedRoot, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                OsConsoleLog.For(typeof(SandboxModuleHosting)).LogSwallowed(error, "cleaning up staged sandbox Docker build context");
            }
        }

        builder.Eventing.Subscribe<BeforeStartEvent>((evt, _) =>
        {
            if (evt.Services.GetService(typeof(Microsoft.Extensions.Hosting.IHostApplicationLifetime))
                is Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime)
            {
                lifetime.ApplicationStopped.Register(Cleanup);
            }

            return Task.CompletedTask;
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static string? ResolveGitHubPackagesToken(string contextPath)
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
            Path.Combine(contextPath, "nuget.config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config"),
        ];
        foreach (var config in configs.Where(File.Exists))
        {
            try
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
            catch (Exception error) when (error is System.Xml.XmlException or IOException)
            {
                OsConsoleLog.For(typeof(SandboxModuleHosting)).LogSwallowed(error, "reading optional NuGet GitHub credentials");
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
            OsConsoleLog.For(typeof(SandboxModuleHosting)).LogSwallowed(error, "running gh auth token");
            return null;
        }

        return null;
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

            if (!_boundKernelUrl)
            {
                _boundKernelUrl = true;
                var kernelHttp = builder.GetEndpoint("http");
                var instance = sandbox.ApplicationBuilder.CreateResourceBuilder(kernelHttp.Resource);
                sandbox.WithReference(kernelHttp)
                    .WithEnvironment("SANDBOX_KERNEL_URL", kernelHttp)
                    .WaitForStart(instance);
            }
        }
    }
}
