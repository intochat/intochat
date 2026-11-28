using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

/// <summary>
/// Owns the single <see cref="EdgeAuthority"/>-scoped system-brain index lookup and mutation path
/// for GitHub installation (<c>installationId</c>) and direct-auth repository (<c>-repositoryId</c>) entries.
/// </summary>
internal static class GitHubRepositoryIndexCoordinator
{
    public static async Task RemoveStaleAsync(
        IServiceProvider? services,
        string connectionKey,
        long currentInstallationId,
        long currentRepositoryId,
        long nextInstallationId,
        long nextRepositoryId)
    {
        var removeOldInstallation = currentInstallationId > 0 && currentInstallationId != nextInstallationId;
        var removeOldRepoIndex = currentRepositoryId > 0 && (currentRepositoryId != nextRepositoryId || nextInstallationId > 0);
        if (!removeOldInstallation && !removeOldRepoIndex)
        {
            return;
        }

        await WithSystemBrainAuthorityAsync(services, async () =>
        {
            if (removeOldInstallation && ResolveIndex(services, currentInstallationId) is { } oldInstIndex)
            {
                await oldInstIndex.Remove(connectionKey).ConfigureAwait(true);
            }

            if (removeOldRepoIndex && ResolveIndex(services, -currentRepositoryId) is { } oldRepoIndex)
            {
                await oldRepoIndex.Remove(connectionKey).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    public static async Task RegisterConnectedAsync(
        IServiceProvider? services,
        string connectionKey,
        long installationId,
        long repositoryId,
        bool isDirectAuth)
    {
        if (installationId <= 0 && (!isDirectAuth || repositoryId <= 0))
        {
            return;
        }

        await WithSystemBrainAuthorityAsync(services, async () =>
        {
            if (installationId > 0 && ResolveIndex(services, installationId) is { } instIndex)
            {
                await instIndex.Add(connectionKey).ConfigureAwait(true);
            }

            if (isDirectAuth && repositoryId > 0 && ResolveIndex(services, -repositoryId) is { } repoIndex)
            {
                await repoIndex.Add(connectionKey).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    public static async Task UnregisterDisconnectedAsync(
        IServiceProvider? services,
        string connectionKey,
        long installationId,
        long repositoryId)
    {
        if (installationId <= 0 && repositoryId <= 0)
        {
            return;
        }

        await WithSystemBrainAuthorityAsync(services, async () =>
        {
            if (installationId > 0 && ResolveIndex(services, installationId) is { } instIndex)
            {
                await instIndex.Remove(connectionKey).ConfigureAwait(true);
            }

            if (repositoryId > 0 && ResolveIndex(services, -repositoryId) is { } repoIndex)
            {
                await repoIndex.Remove(connectionKey).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    // The installation/repository index is a plain neuron in the system brain (06-06d); negative ids index PAT repositories.
    private static IGitHubRepositoryIndex? ResolveIndex(IServiceProvider? services, long installationOrNegRepoId)
    {
        if (services?.GetService(typeof(global::Orleans.IGrainFactory)) is not null)
        {
            return SiloNeurons.Get<IGitHubRepositoryIndex>(services, IGitHubRepositoryIndex.Key(installationOrNegRepoId));
        }

        if (services?.GetService(typeof(Func<long, IGitHubRepositoryIndex>)) is Func<long, IGitHubRepositoryIndex> resolver)
        {
            return resolver(installationOrNegRepoId);
        }

        if (services?.GetService(typeof(INeuronProxies)) is INeuronProxies proxies)
        {
            return proxies.Get<IGitHubRepositoryIndex>(SiloNeurons.IdOf(typeof(IGitHubRepositoryIndex), IGitHubRepositoryIndex.Key(installationOrNegRepoId)));
        }

        return null;
    }

    private static async Task WithSystemBrainAuthorityAsync(IServiceProvider? services, Func<Task> action)
    {
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        using (edge?.Enter(edge.Module(GitHubModule.ManifestName, new BrainId(IGitHubRepositoryIndex.SystemBrain))))
        {
            await action().ConfigureAwait(true);
        }
    }
}
