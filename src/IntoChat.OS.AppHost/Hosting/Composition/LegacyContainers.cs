using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Aspire.Hosting;

// Persistent containers are looked up by name, so a renamed resource starts a second container and the old one
// keeps its host port. The renamed resources remove the old container (never its volume or image) before they start.
internal static class LegacyContainers
{
    public static IReadOnlyList<string> Names(string sha8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha8);
        return
        [
            $"{LegacyNames.Resource("ollama")}-{sha8}",
            $"{LegacyNames.Resource("ollama-openwebui")}-{sha8}",
            $"{LegacyNames.Resource("searxng")}-{sha8}",
        ];
    }

    // The suffix Aspire gives a persistent container comes from the AppHost path hash; empty when it is unavailable.
    public static string Sha8(string? appHostPathSha256)
        => appHostPathSha256 is { Length: >= 10 } ? appHostPathSha256[..8].ToLowerInvariant() : "";

    public static async Task RemoveAsync(IDockerCli docker, string sha8, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        if (sha8.Length == 0) { return; }
        var existing = await docker.ContainerNamesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in Names(sha8).Where(name => existing.Contains(name, StringComparer.Ordinal)))
        {
            await docker.RemoveContainerAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    // A start must not fail because docker could not be asked about an old container.
    public static async Task TryRemoveAsync(IDockerCli docker, string sha8, CancellationToken cancellationToken)
    {
        try
        {
            await RemoveAsync(docker, sha8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OsConsoleLog.For(typeof(LegacyContainers)).LogWarning(ex, "Could not remove containers left under their old names; starting anyway.");
        }
    }

    // Run mode only: the hook never fires in publish, and the old names are a local-machine matter.
    public static IResourceBuilder<T> WithLegacyContainerCleanup<T>(this IResourceBuilder<T> resource, IDistributedApplicationBuilder builder)
        where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.ExecutionContext.IsRunMode) { return resource; }
        var sha8 = Sha8(builder.Configuration["AppHost:PathSha256"]);
        return sha8.Length == 0
            ? resource
            : resource.OnBeforeResourceStarted((_, _, ct) => TryRemoveAsync(new RealDockerCli(), sha8, ct));
    }
}
