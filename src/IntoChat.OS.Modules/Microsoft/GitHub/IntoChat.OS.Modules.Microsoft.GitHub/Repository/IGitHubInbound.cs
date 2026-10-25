using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// Reached only by the webhook ingress, after it verified the delivery's signature. The connection
// neuron's grain type is its connector id, so the default grain type is "github".
[Alias("github.inbound"), Orleans.Metadata.DefaultGrainType("github")]
internal interface IGitHubInbound : IGrainWithStringKey
{
    // The coordinates of a connected repository, or null when it is not connected.
    [ReadOnly] Task<GitHubRepository?> Coordinates();
    Task<bool> AcceptRepositoryEvent(RepositoryEvent receipt, CancellationToken cancellationToken = default);
    Task Revoke(string reason);
}
