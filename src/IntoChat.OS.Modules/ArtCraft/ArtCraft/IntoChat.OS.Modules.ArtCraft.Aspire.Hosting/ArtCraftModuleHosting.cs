using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;

// Each app runs twice: its headless CLI as an MCP server, and its wasm build behind nginx. Images build
// from src/IntoChat.OS.Modules/ArtCraft/docker at a pinned upstream commit, on the fixed ports the
// module options name.
public sealed class ArtCraftModuleHosting : ModuleHosting<ArtCraftModule>
{
    public const string PhotoCraftRef = "d7d5c6069c8f763b8ea3bf1290440bf76a4eed3a";
    public const string FilmCraftRef = "7b6c134472287db4367fca80bcb498240ba36567";

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("artcraft").GetModuleOptions<ArtCraftModuleOptions>("artcraft");
        if (!options.Host) { return; }
        var module = new IntoChatOSModuleBuilder<ArtCraftModuleHosting>(brain);
        var docker = Path.GetFullPath(Path.Combine(brain.ApplicationBuilder.AppHostDirectory, "..", "IntoChat.OS.Modules", "ArtCraft", "docker"));
        foreach (var (app, reference, mcpPort, webPort) in new[]
        {
            ("photocraft", PhotoCraftRef, ArtCraftModuleOptions.PhotoCraftMcpPort, ArtCraftModuleOptions.PhotoCraftWebPort),
            ("filmcraft", FilmCraftRef, ArtCraftModuleOptions.FilmCraftMcpPort, ArtCraftModuleOptions.FilmCraftWebPort),
        })
        {
            var mcp = Image(brain, options, docker, app, "mcp", reference, "craft-mcp.Dockerfile")
                .WithHttpEndpoint(port: mcpPort, targetPort: 8000, name: "mcp", isProxied: false)
                .WithHttpHealthCheck("/status", endpointName: "mcp");
            var web = Image(brain, options, docker, app, "web", reference, "craft-web.Dockerfile")
                .WithHttpEndpoint(port: webPort, targetPort: 80, name: "http", isProxied: false)
                .WithHttpHealthCheck("/", endpointName: "http");
            foreach (var resource in new[] { mcp, web })
            {
                resource.WithLifetime(ContainerLifetime.Persistent).WithParentRelationship(module.Resource);
            }
        }
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
