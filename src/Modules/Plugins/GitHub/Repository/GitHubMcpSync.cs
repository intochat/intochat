using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

/// <summary>
/// Coordinates best-effort synchronization and last-connection teardown of the per-brain
/// GitHub remote MCP connection (<c>connection/mcp/github</c>).
/// </summary>
internal static class GitHubMcpSync
{
    internal const string GitHubMcpUrl = "https://api.githubcopilot.com/mcp/";
    internal const string GitHubMcpTitle = "GitHub MCP";
    internal const string GitHubMcpConnectionKey = "connection/mcp/github";

    internal static bool IsGitHubUserOrPatToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
            && (token.StartsWith("gho_", StringComparison.Ordinal)
                || token.StartsWith("ghu_", StringComparison.Ordinal)
                || token.StartsWith("github_pat_", StringComparison.Ordinal)
                || token.StartsWith("ghp_", StringComparison.Ordinal));

    public static async Task SyncAsync(
        BrainId brain,
        string connectionKey,
        Func<Func<Credential, CancellationToken, Task<bool>>, CancellationToken, Task<bool>> useCredentialAsync,
        Func<NeuronId, IConnection> getConnection,
        ILogger logger)
    {
        try
        {
            await useCredentialAsync(async (credential, _) =>
            {
                if (!IsGitHubUserOrPatToken(credential.Value))
                {
                    return false;
                }

                var payload = JsonSerializer.Serialize(
                    new GitHubMcpConnectPayload(GitHubMcpUrl, GitHubMcpTitle, credential.Value),
                    GitHubAotJsonContext.Default.GitHubMcpConnectPayload);
                var mcpId = new NeuronId(brain, "mcp", GitHubMcpConnectionKey);
                await getConnection(mcpId).Connect("bearer-token", payload).ConfigureAwait(true);
                return true;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Best-effort sync of GitHub remote MCP connection failed for {ConnectionKey}.", connectionKey);
        }
    }

    public static async Task DisconnectIfLastAsync(
        BrainId brain,
        string brainKey,
        string connectionId,
        string connectionKey,
        Func<NeuronId, IConnectionCatalog> getCatalog,
        Func<NeuronId, IConnection> getConnection,
        ILogger logger)
    {
        try
        {
            var hasOtherConnectedGitHub = false;
            try
            {
                var catalog = await getCatalog(IConnectionCatalog.Id(brainKey)).Read().ConfigureAwait(true);
                hasOtherConnectedGitHub = catalog.Rows.Any(row =>
                    string.Equals(row.ConnectorId, GitHubModule.ManifestName, StringComparison.Ordinal)
                    && !string.Equals(row.ConnectionId, connectionId, StringComparison.Ordinal)
                    && row.Status == ConnectionStatus.Connected);
            }
            catch (Exception catalogError) when (catalogError is not OperationCanceledException)
            {
                logger.LogDebug(catalogError, "Connection catalog unavailable when checking remaining GitHub connections for {ConnectionKey}.", connectionKey);
            }

            if (hasOtherConnectedGitHub)
            {
                return;
            }

            var mcpId = new NeuronId(brain, "mcp", GitHubMcpConnectionKey);
            await getConnection(mcpId).Disconnect().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Best-effort disconnect of GitHub remote MCP connection skipped for {ConnectionKey}.", connectionKey);
        }
    }
}
