using DigitalBrain.Modules.Options;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;

// Each app runs twice: its headless CLI as an MCP server, and its wasm build behind nginx (optional in headless/cloud mode).
// Images build from src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Modules/ArtCraft/docker at a pinned upstream commit,
// on the fixed ports the module options name.
public sealed class ArtCraftModuleHosting : ModuleHosting<ArtCraftModule>
{
    public const string PhotoCraftRef = "d7d5c6069c8f763b8ea3bf1290440bf76a4eed3a";
    public const string FilmCraftRef = "7b6c134472287db4367fca80bcb498240ba36567";
    public const int PhotoCraftWebPort = ArtCraftModuleOptions.PhotoCraftWebPort;
    public const int FilmCraftWebPort = ArtCraftModuleOptions.FilmCraftWebPort;
    public const string PrebuiltRegistry = "ghcr.io/intochat";

    // The studios alone, for a run with the ArtCraft module off: the shell's PhotoCraft and FilmCraft apps open these web
    // builds, so they run from the prebuilt images (no Rust build, no MCP servers) on the same fixed ports.
    public static void AddStudios(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = new ArtCraftModuleOptions { ImageRegistry = PrebuiltRegistry };
        foreach (var (app, reference, webPort) in new[] { ("photocraft", PhotoCraftRef, PhotoCraftWebPort), ("filmcraft", FilmCraftRef, FilmCraftWebPort) })
        {
            Web(Image(brain, options, docker: "", app, "web", reference, "craft-web.Dockerfile"), webPort).WithParentRelationship(brain.Resource);
        }
    }

    private static IResourceBuilder<ContainerResource> Web(IResourceBuilder<ContainerResource> web, int port) => web
        .WithHttpEndpoint(port: port, targetPort: 80, name: "http", isProxied: false)
        .WithHttpHealthCheck("/", endpointName: "http")
        .WithLifetime(ContainerLifetime.Persistent);

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("artcraft").GetModuleOptions<ArtCraftModuleOptions>("artcraft");
        if (!options.Host) { return; }
        var module = new IntoChatOSModuleBuilder<ArtCraftModuleHosting>(brain);
        var docker = ResolveDockerContext(brain.ApplicationBuilder.AppHostDirectory);
        foreach (var (app, reference, mcpPort, webPort, webUrl) in new[]
        {
            ("photocraft", PhotoCraftRef, ArtCraftModuleOptions.PhotoCraftMcpPort, PhotoCraftWebPort, options.PhotoCraftWeb),
            ("filmcraft", FilmCraftRef, ArtCraftModuleOptions.FilmCraftMcpPort, FilmCraftWebPort, options.FilmCraftWeb),
        })
        {
            var mcp = Image(brain, options, docker, app, "mcp", reference, "craft-mcp.Dockerfile")
                .WithHttpEndpoint(port: mcpPort, targetPort: 8000, name: "mcp", isProxied: false)
                .WithHttpHealthCheck("/status", endpointName: "mcp")
                .WithVolume("intochat-os-artcraft-work", "/work")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithParentRelationship(module.Resource);
            _ = mcp;

            if (!string.IsNullOrWhiteSpace(webUrl))
            {
                Web(Image(brain, options, docker, app, "web", reference, "craft-web.Dockerfile"), webPort)
                    .WithParentRelationship(module.Resource);
            }
        }
    }

    public static string ResolveDockerContext(string appHostDirectory)
    {
        if (RepositoryRoot.Find() is { } repoRoot)
        {
            var inRepo = Path.Combine(
                repoRoot,
                "src",
                "IntoChat.OS.Aspire",
                "IntoChat.OS.Aspire.Hosting",
                "Modules",
                "ArtCraft",
                "docker");
            if (Directory.Exists(inRepo))
            {
                return inRepo;
            }
        }

        return Path.GetFullPath(Path.Combine(
            appHostDirectory,
            "..",
            "IntoChat.OS.Aspire",
            "IntoChat.OS.Aspire.Hosting",
            "Modules",
            "ArtCraft",
            "docker"));
    }

    // The warm workflow (artcraft-images.yml) publishes ghcr.io/intochat/os-artcraft-<app>-<mcp|web>:<ref12>.
    private static IResourceBuilder<ContainerResource> Image(IntoChatOSBuilder brain, ArtCraftModuleOptions options, string docker, string app, string kind, string reference, string dockerfile)
    {
        var name = brain.ResourceName(app + "-" + kind);
        if (string.IsNullOrWhiteSpace(options.ImageRegistry))
        {
            return brain.ApplicationBuilder.AddDockerfile(name, docker, dockerfile).WithBuildArg("APP", app).WithBuildArg("REF", reference);
        }
        return brain.ApplicationBuilder.AddContainer(name, $"{options.ImageRegistry.TrimEnd('/')}/os-artcraft-{app}-{kind}", reference[..12]);
    }
}
