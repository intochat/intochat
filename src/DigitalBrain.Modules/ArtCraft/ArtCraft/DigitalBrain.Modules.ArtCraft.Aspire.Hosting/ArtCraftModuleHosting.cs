using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.ArtCraft.Aspire.Hosting;

// Each app runs twice: its headless CLI as an MCP server, and its wasm build behind nginx. Images build
// from src/DigitalBrain.Modules/ArtCraft/docker at a pinned upstream commit, on the fixed ports the
// module options name.
public sealed class ArtCraftModuleHosting : ModuleHosting<ArtCraftModule>
{
    public const string PhotoCraftRef = "d7d5c6069c8f763b8ea3bf1290440bf76a4eed3a";
    public const string FilmCraftRef = "7b6c134472287db4367fca80bcb498240ba36567";

    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("artcraft").GetModuleOptions<ArtCraftModuleOptions>("artcraft");
        if (!options.Host) { return; }
        var module = new DigitalBrainModuleBuilder<ArtCraftModuleHosting>(brain);
        var docker = Path.GetFullPath(Path.Combine(brain.ApplicationBuilder.AppHostDirectory, "..", "DigitalBrain.Modules", "ArtCraft", "docker"));
        foreach (var (app, reference, mcpPort, webPort) in new[]
        {
            ("photocraft", PhotoCraftRef, ArtCraftModuleOptions.PhotoCraftMcpPort, ArtCraftModuleOptions.PhotoCraftWebPort),
            ("filmcraft", FilmCraftRef, ArtCraftModuleOptions.FilmCraftMcpPort, ArtCraftModuleOptions.FilmCraftWebPort),
        })
        {
            brain.ApplicationBuilder.AddDockerfile(brain.ResourceName(app + "-mcp"), docker, "craft-mcp.Dockerfile")
                .WithBuildArg("APP", app).WithBuildArg("REF", reference)
                .WithHttpEndpoint(port: mcpPort, targetPort: 8000, name: "mcp", isProxied: false)
                .WithHttpHealthCheck("/status", endpointName: "mcp")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithParentRelationship(module.Resource);
            brain.ApplicationBuilder.AddDockerfile(brain.ResourceName(app + "-web"), docker, "craft-web.Dockerfile")
                .WithBuildArg("APP", app).WithBuildArg("REF", reference)
                .WithHttpEndpoint(port: webPort, targetPort: 80, name: "http", isProxied: false)
                .WithHttpHealthCheck("/", endpointName: "http")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithParentRelationship(module.Resource);
        }
    }
}
