using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

public sealed partial class SettingsApp
{
    public const string DefaultWebhookBaseUrl = "http://localhost:5080";
    public const string DefaultMunclyCrmUrl = "https://mcpdemo-crm.munc.ly";
    public const string MunclyMcpServerName = "muncly-crm";

    private static readonly SettingsConnectorOffer[] DefaultConnectorOffers =
    [
        new("github", "GitHub", ["device-flow", "oauth", "pat"], "GitHub"),
        new("muncly", "Muncly CRM", ["bearer-token"], "Muncly CRM"),
        new("google-drive", "Google Drive", ["oauth"], "Google Drive"),
        new("gmail", "Gmail", ["oauth"], "Gmail"),
        new("salesforce", "Salesforce", ["oauth"], "Salesforce"),
        new("mcp", "MCP Servers", ["bearer-token"], "MCP"),
    ];

    private static string? MapApiKeyFieldToProvider(string field) => field.Trim().ToUpperInvariant() switch
    {
        "ANTHROPIC_API_KEY" => "anthropic",
        "OPENAI_API_KEY" => "openai",
        "GOOGLE_API_KEY" => "google",
        "OPENROUTER_API_KEY" => "openrouter",
        "XAI_API_KEY" => "xai",
        _ => null,
    };

    private async Task<string?> TryOpenSecretRefAsync(string? secretRef, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return null;
        }

        var trimmedRef = secretRef.Trim();
        var vault = SecretVault ?? Vault;
        if (vault is not null)
        {
            try
            {
                var opened = await vault.TryOpenSecretAsync(trimmedRef);
                if (!string.IsNullOrWhiteSpace(opened))
                {
                    return opened.Trim();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "ISecretVault.TryOpenSecretAsync failed for {Field}.", fieldName);
            }
        }

        if (SecretVaultPort is { } port)
        {
            try
            {
                var resolved = await port.Resolve(
                    new SecretRef { Reference = trimmedRef, Label = fieldName, Status = SecretStatus.Set },
                    CancellationToken.None);
                if (resolved.Status == SecretVaultStatus.Ok && !string.IsNullOrWhiteSpace(resolved.Value))
                {
                    return resolved.Value.Trim();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "ISecretVaultPort.Resolve failed for {Field}.", fieldName);
            }
        }

        return null;
    }

    // The API keys form's client stores each typed key in the person's vault; only SecretRef handles arrive here.
    // Unseal each submitted ref via ISecretVault / ISecretVaultPort and persist AI provider credentials directly to ModelCatalog
    // without ever storing raw keys in SettingsAppState, logs, or snapshots.
    private async Task SealApiKeysAsync(IReadOnlyDictionary<string, string>? secretRefs)
    {
        var hasBrain = TryBrainOrNull() is { Value.Length: > 0 };
        var catalog = hasBrain ? ModelCatalog : null;
        var sealedCount = 0;

        if (secretRefs is { Count: > 0 })
        {
            foreach (var (field, secretRef) in secretRefs)
            {
                if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(secretRef))
                {
                    continue;
                }

                sealedCount++;
                if (MapApiKeyFieldToProvider(field) is not { } provider || catalog is null)
                {
                    continue;
                }

                var unsealed = await TryOpenSecretRefAsync(secretRef, field);
                if (string.IsNullOrWhiteSpace(unsealed))
                {
                    continue;
                }

                try
                {
                    await catalog.SaveProviderCredential(provider, unsealed);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "IModelCatalog.SaveProviderCredential failed for provider '{Provider}'.", provider);
                }
            }
        }

        var secrets = await InspectSecretStatusesAsync(State.Variables ?? []);
        var modelInfo = await TryReadModelCatalogAsync(State.DefaultModel);
        await UpdateAsync(State with
        {
            SecretStatuses = secrets,
            Roles = modelInfo.Roles.Length > 0 ? modelInfo.Roles : State.Roles,
            Providers = modelInfo.Providers.Length > 0 ? modelInfo.Providers : State.Providers,
            LocalOllamaEndpoint = modelInfo.LocalEndpoint,
            Device = modelInfo.Device ?? State.Device,
            Status = $"Sealed {sealedCount} API key{(sealedCount == 1 ? "" : "s")} in your vault.",
        });
    }

    [On]
    public async Task StartDeviceFlow(SettingsStartDeviceFlow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (OAuthDeviceFlow is not { } flow)
        {
            await UpdateAsync(State with
            {
                DeviceFlowStatus = "OAuth Device Flow is unavailable in this host.",
                Status = "OAuth Device Flow is unavailable in this host.",
            });
            return;
        }

        if (TryBrainOrNull() is not { Value.Length: > 0 } brain)
        {
            await UpdateAsync(State with { Status = "Brain context is unavailable for Device Flow." });
            return;
        }

        var connectorId = string.IsNullOrWhiteSpace(request.ConnectorId) ? "github" : request.ConnectorId.Trim().ToLowerInvariant();
        var connectionId = SettingsAppState.NormalizeConnectionSlug(request.ConnectionId, fallback: "main");
        var authMethodId = string.IsNullOrWhiteSpace(request.AuthMethodId) ? "device-flow" : request.AuthMethodId.Trim();
        var connectionKey = ConnectionKeys.For(brain.Value, connectorId, connectionId);

        try
        {
            var started = await flow.StartAsync(brain, connectionKey, authMethodId, request.Target);
            var verifyUrl = !string.IsNullOrWhiteSpace(started.VerificationUriComplete)
                ? started.VerificationUriComplete
                : started.VerificationUri;
            await UpdateAsync(State with
            {
                Section = "connections",
                DeviceFlowId = started.FlowId,
                DeviceFlowUserCode = started.UserCode,
                DeviceFlowVerificationUri = verifyUrl,
                DeviceFlowStatus = $"Enter code {started.UserCode} at {started.VerificationUri}, then click Complete Sign-In.",
                Status = $"Device Flow started · Code: {started.UserCode}",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IOAuthDeviceFlow.StartAsync failed for {ConnectionKey}.", connectionKey);
            await UpdateAsync(State with
            {
                DeviceFlowStatus = $"Could not start Device Flow: {error.Message}",
                Status = $"Could not start Device Flow: {error.Message}",
            });
        }
    }

    [On]
    public async Task CompleteDeviceFlow(SettingsCompleteDeviceFlow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flowId = !string.IsNullOrWhiteSpace(request.FlowId) ? request.FlowId.Trim() : State.DeviceFlowId;
        if (OAuthDeviceFlow is not { } flow || string.IsNullOrWhiteSpace(flowId))
        {
            await UpdateAsync(State with { Status = "No active Device Flow to complete." });
            return;
        }

        if (TryBrainOrNull() is not { Value.Length: > 0 } brain)
        {
            await UpdateAsync(State with { Status = "Brain context is unavailable for Device Flow." });
            return;
        }

        try
        {
            var poll = await flow.PollOnceAsync(brain, flowId);
            switch (poll.Status)
            {
                case OAuthDevicePollStatus.Connected:
                    {
                        var refreshed = await RefreshConnectionsProjectionAsync(State with
                        {
                            DeviceFlowId = null,
                            DeviceFlowUserCode = null,
                            DeviceFlowVerificationUri = null,
                            DeviceFlowStatus = null,
                        });
                        if (poll.Connection is { } conn)
                        {
                            var key = $"connection/{conn.ConnectorId}/{conn.ConnectionId}";
                            var row = new SettingsConnectionRow(
                                Key: key,
                                ConnectorId: conn.ConnectorId,
                                ConnectionId: conn.ConnectionId,
                                DisplayName: ResolveConnectorDisplayName(conn.ConnectorId, conn.ConnectionId),
                                Status: conn.Status,
                                Account: conn.Account,
                                LastProbedAt: conn.LastProbedAt,
                                Detail: conn.Detail);
                            refreshed = refreshed with { ActiveConnections = UpsertConnectionRow(refreshed.ActiveConnections, row) };
                        }

                        await UpdateAsync(refreshed with { Status = "Connected via OAuth Device Flow." });
                        return;
                    }
                case OAuthDevicePollStatus.Pending:
                    await UpdateAsync(State with
                    {
                        DeviceFlowStatus = "Authorization still pending — complete sign-in in your browser, then click Complete Sign-In.",
                        Status = "Device Flow authorization still pending.",
                    });
                    return;
                default:
                    await UpdateAsync(State with
                    {
                        DeviceFlowId = null,
                        DeviceFlowUserCode = null,
                        DeviceFlowVerificationUri = null,
                        DeviceFlowStatus = poll.Error ?? $"Device Flow {poll.Status.ToString().ToLowerInvariant()}.",
                        Status = poll.Error ?? $"Device Flow {poll.Status.ToString().ToLowerInvariant()}.",
                    });
                    return;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IOAuthDeviceFlow.PollOnceAsync failed for flow {FlowId}.", flowId);
            await UpdateAsync(State with
            {
                DeviceFlowStatus = $"Device Flow poll failed: {error.Message}",
                Status = $"Device Flow poll failed: {error.Message}",
            });
        }
    }

    [On]
    public async Task ProbeConnection(SettingsProbeConnection request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check(
                "probe-connection",
                SettingsAppState.ValidateConnectionKey(request.ConnectorId),
                SettingsAppState.ValidateConnectionKey(request.ConnectionId)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var connectorId = request.ConnectorId.Trim().ToLowerInvariant();
        var connectionId = request.ConnectionId.Trim();
        var key = $"connection/{connectorId}/{connectionId}";
        ConnectionView? probed = null;
        string status;

        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var connId = new NeuronId(brain, connectorId, key);
            try
            {
                probed = await Connections[connId].Probe();
                status = $"Probed {connectorId}/{connectionId}: {probed.Status}.";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnection.Probe failed for {ConnectionKey}.", key);
                status = $"Probe failed for {connectorId}/{connectionId}: {error.Message}";
            }
        }
        else
        {
            status = $"Brain context is unavailable for {connectorId}/{connectionId}.";
        }

        var next = await RefreshConnectionsProjectionAsync(State);
        if (probed is not null)
        {
            var updatedRow = new SettingsConnectionRow(
                Key: key,
                ConnectorId: connectorId,
                ConnectionId: connectionId,
                DisplayName: ResolveConnectorDisplayName(connectorId, connectionId),
                Status: probed.Status,
                Account: probed.Account,
                ToolCount: next.ActiveConnections.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.ToolCount ?? 0,
                Endpoint: next.ActiveConnections.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Endpoint,
                LastProbedAt: probed.LastProbedAt,
                Detail: probed.Detail);
            next = next with { ActiveConnections = UpsertConnectionRow(next.ActiveConnections, updatedRow) };
        }

        await UpdateAsync(next with { Status = status });
    }

    [On]
    public async Task DisconnectConnection(SettingsDisconnectConnection request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check(
                "disconnect-connection",
                SettingsAppState.ValidateConnectionKey(request.ConnectorId),
                SettingsAppState.ValidateConnectionKey(request.ConnectionId)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var connectorId = request.ConnectorId.Trim().ToLowerInvariant();
        var connectionId = request.ConnectionId.Trim();
        var key = $"connection/{connectorId}/{connectionId}";

        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var connId = new NeuronId(brain, connectorId, key);
            try
            {
                await Connections[connId].Disconnect();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnection.Disconnect failed for {ConnectionKey}.", key);
            }

            if (string.Equals(connectorId, IMcpToolConnection.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await McpServers[IMcpToolConnection.NeuronIdOf(brain, connectionId)].DisconnectServer();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "IMcpToolConnection.DisconnectServer failed for {ConnectionId}.", connectionId);
                }
            }
        }

        var remaining = (State.ActiveConnections ?? [])
            .Where(r => !string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mcpRows = string.Equals(connectorId, IMcpToolConnection.TypeName, StringComparison.OrdinalIgnoreCase)
            ? (State.McpServers ?? [])
                .Select(row => string.Equals(row.Name, connectionId, StringComparison.OrdinalIgnoreCase)
                    ? row with { Connected = false, ToolCount = 0 }
                    : row)
                .ToArray()
            : State.McpServers;

        var refreshed = await RefreshConnectionsProjectionAsync(State with
        {
            ActiveConnections = remaining,
            McpServers = mcpRows,
        });

        await UpdateAsync(refreshed with
        {
            ActiveConnections = refreshed.ActiveConnections
                .Where(r => !string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) || r.Status != ConnectionStatus.Disconnected)
                .ToArray(),
            Status = $"Disconnected {connectorId}/{connectionId}.",
        });
    }

    // The PAT form's secret fields arrive as vault references (secret://{owner}/{name}); typed secrets never reach this screen.
    private async Task ConnectGitHubAsync(string? rawRepository, string? rawSlug, IReadOnlyDictionary<string, string>? secretRefs)
    {
        if (Decision.Check("connect-github-pat", SettingsAppState.ValidateGitHubRepositoryRule(rawRepository)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var normalizedRepo = SettingsAppState.NormalizeGitHubRepository(rawRepository)!;
        var slug = SettingsAppState.NormalizeConnectionSlug(rawSlug, fallback: "main");
        var connectionKey = $"connection/github/{slug}";

        var patRef = secretRefs is not null && secretRefs.TryGetValue("PersonalAccessToken", out var pRef) ? pRef : null;
        var webhookRef = secretRefs is not null && secretRefs.TryGetValue("WebhookSecret", out var wRef) ? wRef : null;

        var unsealedPat = await TryOpenSecretRefAsync(patRef, "PersonalAccessToken");
        if (string.IsNullOrWhiteSpace(unsealedPat))
        {
            var reason = string.IsNullOrWhiteSpace(patRef)
                ? "Personal Access Token is required to connect GitHub."
                : "Failed to unseal GitHub Personal Access Token from vault.";
            await RecordGitHubConnectionOutcomeAsync(connectionKey, slug, normalizedRepo, ConnectionStatus.Failing, reason);
            return;
        }

        string? unsealedWebhookSecret = null;
        if (!string.IsNullOrWhiteSpace(webhookRef))
        {
            unsealedWebhookSecret = await TryOpenSecretRefAsync(webhookRef, "WebhookSecret");
            if (string.IsNullOrWhiteSpace(unsealedWebhookSecret))
            {
                await RecordGitHubConnectionOutcomeAsync(
                    connectionKey,
                    slug,
                    normalizedRepo,
                    ConnectionStatus.Failing,
                    "Failed to unseal GitHub Webhook Secret from vault.");
                return;
            }
        }

        var responsePayload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_kind"] = "pat",
            ["repository"] = normalizedRepo,
        };
        if (!string.IsNullOrWhiteSpace(unsealedWebhookSecret))
        {
            responsePayload["webhookSecret"] = unsealedWebhookSecret;
        }

        var responseJson = JsonSerializer.Serialize(responsePayload);
        ConnectionStatus status;
        string? detail = !string.IsNullOrWhiteSpace(unsealedWebhookSecret) ? "Webhook secret sealed" : null;

        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var connId = new NeuronId(brain, "github", connectionKey);
            try
            {
                var grantView = await ConnectionInbounds[connId].AcceptGrant(new OAuthGrant(
                    AuthMethodId: "pat",
                    AccessToken: unsealedPat,
                    RefreshToken: null,
                    ExpiresAt: null,
                    Scopes: "issues:write,pull_requests:write,contents:write,metadata:read",
                    Target: normalizedRepo,
                    Response: responseJson));
                status = grantView.Status;
                detail = grantView.Detail ?? detail;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnectionInbound.AcceptGrant failed on {ConnectionKey}; trying IConnection.Connect.", connectionKey);
                try
                {
                    var connectPayload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["repository"] = normalizedRepo,
                        ["token"] = unsealedPat,
                    };
                    if (!string.IsNullOrWhiteSpace(unsealedWebhookSecret))
                    {
                        connectPayload["webhookSecret"] = unsealedWebhookSecret;
                    }

                    var connView = await Connections[connId].Connect("pat", JsonSerializer.Serialize(connectPayload));
                    status = connView.Status;
                    detail = connView.Detail ?? detail;
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    Log.LogWarning(inner, "Failed to connect GitHub repository {Repository} on {ConnectionKey}.", normalizedRepo, connectionKey);
                    status = ConnectionStatus.Failing;
                    detail = inner.Message;
                }
            }
        }
        else
        {
            status = ConnectionStatus.Failing;
            detail = "Brain context is unavailable for GitHub connection.";
        }

        await RecordGitHubConnectionOutcomeAsync(connectionKey, slug, normalizedRepo, status, detail);
    }

    private async Task RecordGitHubConnectionOutcomeAsync(
        string connectionKey,
        string slug,
        string normalizedRepo,
        ConnectionStatus status,
        string? detail)
    {
        var row = new SettingsConnectionRow(
            Key: connectionKey,
            ConnectorId: "github",
            ConnectionId: slug,
            DisplayName: $"GitHub · {slug}",
            Status: status,
            Account: normalizedRepo,
            Endpoint: $"{ResolveWebhookBaseUrl()}/integrations/github/webhook",
            Detail: detail);

        var refreshed = await RefreshConnectionsProjectionAsync(State with
        {
            Section = "connections",
            SubPage = null,
            ActiveConnections = UpsertConnectionRow(State.ActiveConnections, row),
        });

        var statusText = status == ConnectionStatus.Connected
            ? $"Connected GitHub repository {normalizedRepo} ({connectionKey})."
            : $"Failed to connect GitHub repository {normalizedRepo}: {detail ?? "Connection failed."}";

        await UpdateAsync(refreshed with
        {
            Title = "Settings · connections",
            Status = statusText,
        });
    }

    private async Task ConnectMunclyAsync(string? rawUrl, IReadOnlyDictionary<string, string>? secretRefs)
    {
        var candidateUrl = string.IsNullOrWhiteSpace(rawUrl) ? DefaultMunclyCrmUrl : rawUrl.Trim();
        if (Decision.Check("connect-muncly", SettingsAppState.ValidateMcpServerUrlRule(candidateUrl)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var mcpUrl = NormalizeMunclyMcpUrl(candidateUrl);
        var tokenRef = secretRefs is not null && secretRefs.TryGetValue("BearerToken", out var bRef) ? bRef : null;
        await ConnectMcpServerCoreAsync(MunclyMcpServerName, mcpUrl, "Muncly CRM", tokenRef, navigateToConnections: true);
    }

    private async Task McpAddAsync(string? rawName, string? rawUrl, IReadOnlyDictionary<string, string>? secretRefs, string? rawTitle = null)
    {
        if (Decision.Check(
                "mcp-add",
                SettingsAppState.ValidateMcpServerNameRule(rawName),
                SettingsAppState.ValidateMcpServerUrlRule(rawUrl)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var name = rawName!.Trim().ToLowerInvariant();
        var url = rawUrl!.Trim();
        var title = string.IsNullOrWhiteSpace(rawTitle) ? name : rawTitle.Trim();
        var tokenRef = secretRefs is not null && secretRefs.TryGetValue("token", out var tRef) ? tRef : null;
        await ConnectMcpServerCoreAsync(name, url, title, tokenRef, navigateToConnections: false);
    }

    private async Task ConnectMcpServerCoreAsync(
        string name,
        string url,
        string title,
        string? tokenRef,
        bool navigateToConnections)
    {
        McpServerRow mcpRow;
        ConnectionStatus connStatus = ConnectionStatus.Disconnected;
        string? connDetail = null;
        string statusMessage;

        if (TryBrainOrNull() is { Value.Length: > 0 } brain)
        {
            var serverId = IMcpToolConnection.NeuronIdOf(brain, name);
            try
            {
                McpServerState? serverState = null;
                if (ConnectionSecretBinder is { } binder && !string.IsNullOrWhiteSpace(tokenRef))
                {
                    var fullConnectionKey = ConnectionKeys.For(brain.Value, IMcpToolConnection.TypeName, name);
                    var connView = await binder.BindAsync(
                        brain,
                        IMcpToolConnection.TypeName,
                        fullConnectionKey,
                        "bearer-token",
                        tokenRef,
                        formatValue: token => SerializeMcpBearerPayload(url, title, token),
                        probe: true);
                    connStatus = connView.Status;
                    connDetail = connView.Detail;
                    try
                    {
                        serverState = await McpServers[serverId].ReadState();
                    }
                    catch (Exception readError) when (readError is not OperationCanceledException)
                    {
                        Log.LogDebug(readError, "IMcpToolConnection.ReadState after binder BindAsync failed for '{ServerName}'.", name);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(tokenRef))
                {
                    var bearerToken = await TryOpenSecretRefAsync(tokenRef, "token");
                    if (string.IsNullOrWhiteSpace(bearerToken))
                    {
                        throw new InvalidOperationException("Failed to unseal MCP Bearer token from vault.");
                    }

                    var payload = SerializeMcpBearerPayload(url, title, bearerToken);
                    var connView = await Connections[serverId].Connect("bearer-token", payload);
                    try
                    {
                        connView = await Connections[serverId].Probe();
                    }
                    catch (Exception probeError) when (probeError is not OperationCanceledException)
                    {
                        Log.LogDebug(probeError, "IConnection.Probe after bearer-token Connect failed for MCP server '{ServerName}'.", name);
                    }

                    connStatus = connView.Status;
                    connDetail = connView.Detail;
                    try
                    {
                        serverState = await McpServers[serverId].ReadState();
                    }
                    catch (Exception readError) when (readError is not OperationCanceledException)
                    {
                        Log.LogDebug(readError, "IMcpToolConnection.ReadState after bearer-token Connect failed for '{ServerName}'.", name);
                    }
                }
                else
                {
                    serverState = await McpServers[serverId].ConnectServer(new McpConnectionConfig(url, title));
                    connStatus = serverState.Connected ? ConnectionStatus.Connected : ConnectionStatus.Failing;
                    connDetail = serverState.Error;
                }

                var isConnected = (serverState?.Connected ?? false) || connStatus == ConnectionStatus.Connected;
                var toolCount = serverState?.Tools?.Length ?? 0;
                var effectiveTitle = !string.IsNullOrWhiteSpace(serverState?.Title) ? serverState!.Title : title;
                var effectiveUrl = !string.IsNullOrWhiteSpace(serverState?.Url) ? serverState!.Url : url;
                var effectiveError = serverState?.Error ?? (isConnected ? null : connDetail);

                mcpRow = new McpServerRow(
                    Name: name,
                    Title: effectiveTitle,
                    Url: effectiveUrl,
                    Connected: isConnected,
                    ToolCount: toolCount,
                    Error: effectiveError);
                connStatus = isConnected ? ConnectionStatus.Connected : (connStatus == ConnectionStatus.Expired ? ConnectionStatus.Expired : ConnectionStatus.Failing);
                statusMessage = isConnected
                    ? $"Connected MCP server '{effectiveTitle}' ({toolCount} tool(s))."
                    : $"MCP server '{effectiveTitle}' is disconnected.";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogWarning(error, "Failed to connect MCP server '{ServerName}' at {Url}.", name, url);
                mcpRow = new McpServerRow(name, title, url, Connected: false, ToolCount: 0, Error: error.Message);
                connStatus = ConnectionStatus.Failing;
                connDetail = error.Message;
                statusMessage = $"Failed to connect MCP server '{name}': {error.Message}";
            }
        }
        else
        {
            mcpRow = new McpServerRow(name, title, url, Connected: false, ToolCount: 0, Error: "Brain context is unavailable.");
            connStatus = ConnectionStatus.Disconnected;
            connDetail = "Brain context is unavailable.";
            statusMessage = $"Brain context is unavailable for MCP server '{name}'.";
        }

        var connRow = new SettingsConnectionRow(
            Key: $"connection/mcp/{name}",
            ConnectorId: IMcpToolConnection.TypeName,
            ConnectionId: name,
            DisplayName: mcpRow.Title,
            Status: connStatus,
            Account: mcpRow.Url,
            ToolCount: mcpRow.ToolCount,
            Endpoint: mcpRow.Url,
            Detail: connDetail ?? mcpRow.Error);

        var next = State with
        {
            Section = navigateToConnections ? "connections" : State.Section,
            McpServers = UpsertMcpRow(State.McpServers, mcpRow),
            ActiveConnections = UpsertConnectionRow(State.ActiveConnections, connRow),
            WebhookBaseUrl = ResolveWebhookBaseUrl(),
            AvailableConnectors = ResolveAvailableConnectors(),
            Status = statusMessage,
        };

        await UpdateAsync(next);
    }

    private async Task<SettingsAppState> RefreshConnectionsProjectionAsync(SettingsAppState baseline)
    {
        var byKey = new Dictionary<string, SettingsConnectionRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in baseline.ActiveConnections ?? [])
        {
            if (!string.IsNullOrWhiteSpace(existing.Key))
            {
                byKey[existing.Key] = existing;
            }
        }

        if (TryBrainOrNull() is { Value.Length: > 0 } && ConnectionCatalog is { } catalog)
        {
            try
            {
                var snapshot = await catalog.Read();
                foreach (var row in snapshot.Rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Key))
                    {
                        continue;
                    }

                    var key = row.Key.Trim();
                    byKey.TryGetValue(key, out var prev);
                    byKey[key] = new SettingsConnectionRow(
                        Key: key,
                        ConnectorId: row.ConnectorId,
                        ConnectionId: row.ConnectionId,
                        DisplayName: ResolveConnectorDisplayName(row.ConnectorId, row.ConnectionId),
                        Status: row.Status,
                        Account: row.Account ?? prev?.Account,
                        ToolCount: prev?.ToolCount ?? 0,
                        Endpoint: prev?.Endpoint,
                        LastProbedAt: row.LastProbedAt ?? prev?.LastProbedAt,
                        Detail: prev?.Detail);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnectionCatalog.Read is optional when SettingsApp runs in standalone tests.");
            }
        }

        var mcpServers = await TryReadMcpServersAsync(baseline.McpServers);
        foreach (var mcp in mcpServers)
        {
            var key = $"connection/mcp/{mcp.Name}";
            byKey.TryGetValue(key, out var prev);
            var mcpStatus = mcp.Connected
                ? ConnectionStatus.Connected
                : !string.IsNullOrWhiteSpace(mcp.Error)
                    ? ConnectionStatus.Failing
                    : prev?.Status ?? ConnectionStatus.Disconnected;

            byKey[key] = new SettingsConnectionRow(
                Key: key,
                ConnectorId: IMcpToolConnection.TypeName,
                ConnectionId: mcp.Name,
                DisplayName: string.IsNullOrWhiteSpace(mcp.Title) ? ResolveConnectorDisplayName("mcp", mcp.Name) : mcp.Title,
                Status: mcpStatus,
                Account: !string.IsNullOrWhiteSpace(mcp.Url) ? mcp.Url : prev?.Account,
                ToolCount: mcp.ToolCount,
                Endpoint: !string.IsNullOrWhiteSpace(mcp.Url) ? mcp.Url : prev?.Endpoint,
                LastProbedAt: prev?.LastProbedAt,
                Detail: mcp.Error ?? prev?.Detail);
        }

        return baseline with
        {
            ActiveConnections = [.. byKey.Values.OrderBy(static r => r.Key, StringComparer.OrdinalIgnoreCase)],
            AvailableConnectors = ResolveAvailableConnectors(),
            McpServers = mcpServers,
            WebhookBaseUrl = ResolveWebhookBaseUrl(),
        };
    }

    private SettingsConnectorOffer[] ResolveAvailableConnectors()
    {
        if (Connectors is not { Count: > 0 })
        {
            return DefaultConnectorOffers;
        }

        var offers = Connectors
            .Where(static c => !string.IsNullOrWhiteSpace(c.Id))
            .Select(static c => new SettingsConnectorOffer(
                c.Id,
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Id : c.DisplayName,
                [.. (c.AuthMethods ?? []).Concat((c.DeviceMethods ?? []).Select(static d => d.Id)).Distinct(StringComparer.OrdinalIgnoreCase)],
                string.IsNullOrWhiteSpace(c.ExternalSystem) ? c.DisplayName : c.ExternalSystem))
            .OrderBy(static c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return offers.Length > 0 ? offers : DefaultConnectorOffers;
    }

    private static string SerializeMcpBearerPayload(string url, string title, string token)
        => JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = url,
            ["title"] = title,
            ["header"] = "Authorization",
            ["prefix"] = "Bearer ",
            ["token"] = token,
        });

    private string ResolveWebhookBaseUrl()
    {
        var configuredWebhook = Configuration?["IntoChatOS:Modules:github:Options:PublicWebhookUrl"];
        if (!string.IsNullOrWhiteSpace(configuredWebhook) && Uri.TryCreate(configuredWebhook.Trim(), UriKind.Absolute, out var webhookUri))
        {
            return webhookUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        var publicBase = Configuration?["IntoChatOS:Modules:auth:Options:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(publicBase) && Uri.TryCreate(publicBase.Trim(), UriKind.Absolute, out var publicUri))
        {
            return publicUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return DefaultWebhookBaseUrl;
    }

    internal static string NormalizeMunclyMcpUrl(string rawUrl)
    {
        var uri = new Uri(rawUrl.Trim(), UriKind.Absolute);
        if (string.IsNullOrEmpty(uri.AbsolutePath) || string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/api/mcp";
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string ResolveConnectorDisplayName(string connectorId, string connectionId)
        => connectorId.Trim().ToLowerInvariant() switch
        {
            "github" => $"GitHub · {connectionId}",
            "google-drive" => $"Google Drive · {connectionId}",
            "gmail" => $"Gmail · {connectionId}",
            "salesforce" => $"Salesforce · {connectionId}",
            "mcp" when string.Equals(connectionId, MunclyMcpServerName, StringComparison.OrdinalIgnoreCase) => "Muncly CRM",
            "mcp" => $"MCP · {connectionId}",
            _ => $"{connectorId} · {connectionId}",
        };

    private static SettingsConnectionRow[] UpsertConnectionRow(SettingsConnectionRow[]? existing, SettingsConnectionRow row)
    {
        var byKey = (existing ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(static r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
        byKey[row.Key] = row;
        return [.. byKey.Values.OrderBy(static r => r.Key, StringComparer.OrdinalIgnoreCase)];
    }
}
