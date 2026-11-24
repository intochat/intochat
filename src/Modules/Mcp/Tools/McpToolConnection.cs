using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Confirmation;
using DigitalBrain.Limits;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace IntoChat.OS.Modules.Mcp;

[GenerateSerializer, Alias("mcp.tool-connection-state")]
internal sealed record McpToolConnectionState : ConnectionState
{
    [Id(0)] public string Title { get; set; } = "";
    [Id(1)] public string Url { get; set; } = "";
    [Id(2)] public bool Connected { get; set; }
    [Id(3)] public ToolDescriptor[] Tools { get; set; } = [];
    [Id(4)] public string? LastError { get; set; }
    [Id(5)] public DateTimeOffset? ProbedAt { get; set; }
    [Id(6)] public string? HeaderName { get; set; }
    [Id(7)] public string? HeaderPrefix { get; set; }
    [Id(8)] public string[]? AllowedTools { get; set; }
    [Id(9)] public McpConfirmationMode ConfirmationMode { get; set; } = McpConfirmationMode.None;
}

// One MCP tool connection per brain, keyed {brain}/connection/mcp/{name}.
// Plain configuration (URL, title, header name/prefix, AllowedTools, ConfirmationMode) lives in state;
// the optional secret token stays sealed inside ConnectionNeuron's vault and never leaves UseCredentialAsync.
[Neuron(IMcpToolConnection.TypeName, State = "mcp.tool")]
internal sealed partial class McpToolConnection : ConnectionNeuron<McpToolConnectionState>, IMcpToolConnection
{
    private sealed record PendingConfirmation(string SessionKey, string Tool, McpConfirmationMode Mode);

    private readonly ILogger _log;
    private readonly McpClientSession _session;
    private readonly HashSet<string> _approvedSessions = new(StringComparer.Ordinal);
    private readonly HashSet<(string SessionKey, string Tool)> _approvedSingleCalls = [];
    private readonly Dictionary<string, PendingConfirmation> _pendingByPlanId = new(StringComparer.Ordinal);

    [Slot(Key = "default", Required = false)]
    private partial IPolicies? Policies { get; }

    [Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)]
    private partial IConfirmationRequests? Confirmations { get; }

    public McpToolConnection() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public McpToolConnection(
        HttpMessageHandler? httpHandler = null,
        ILogger<McpToolConnection>? logger = null)
    {
        _log = logger ?? McpConsoleLog.For<McpToolConnection>();
        _session = new McpClientSession(httpHandler);
    }

    public Task<IReadOnlyList<string>> List()
    {
        var tools = Snapshot.Connected ? McpClientSession.FilterAllowedTools(Snapshot.Tools, Snapshot.AllowedTools) : [];
        IReadOnlyList<string> list = tools.Select(t => t.Name).ToList();
        return Task.FromResult(list);
    }

    public Task<ToolSet> Tools(CancellationToken ct = default)
        => Task.FromResult(Snapshot.Connected
            ? new ToolSet(Snapshot.Title, McpClientSession.FilterAllowedTools(Snapshot.Tools, Snapshot.AllowedTools))
            : new ToolSet("", []));

    public Task<McpServerState> ReadState(CancellationToken ct = default)
        => Task.FromResult(ToServerState(Snapshot));

    public Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default)
    {
        var visible = Snapshot.Connected ? McpClientSession.FilterAllowedTools(Snapshot.Tools, Snapshot.AllowedTools) : [];
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>(visible);
        }

        var trimmed = query.Trim();
        var matches = visible.Where(t =>
            t.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        return Task.FromResult<IReadOnlyList<ToolDescriptor>>(matches);
    }

    public Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool) || !Snapshot.Connected || !McpClientSession.IsToolAllowed(tool, Snapshot.AllowedTools))
        {
            return Task.FromResult(false);
        }

        var descriptor = Snapshot.Tools.FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return Task.FromResult(false);
        }

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public async Task<McpServerState> ConnectServer(McpConnectionConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var endpoint = McpClientSession.ValidateEndpoint(config.Url);
        var normalized = NormalizeConfig(config, endpoint);
        ValidateHeaderName(normalized.HeaderName);

        await _session.DropAsync().ConfigureAwait(false);
        ClearConfirmationState();
        ToolDescriptor[] tools;
        try
        {
            tools = await _session.ListToolsAsync(endpoint, CreateClientAsync, normalized.AllowedTools, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failed = Snapshot with
            {
                Url = normalized.Url,
                Title = normalized.Title,
                HeaderName = normalized.HeaderName,
                HeaderPrefix = normalized.HeaderPrefix,
                AllowedTools = normalized.AllowedTools,
                ConfirmationMode = normalized.ConfirmationMode,
                Connected = false,
                Tools = [],
                LastError = error.Message,
                ProbedAt = NowUtc(),
                Status = ConnectionStatus.Failing,
                Detail = error.Message,
            };
            await SaveServerStateAsync(failed, publishConnectionEvent: false).ConfigureAwait(false);
            throw new InvalidOperationException($"MCP server {normalized.Url} is unreachable: {error.Message}", error);
        }

        var connected = Snapshot with
        {
            Url = normalized.Url,
            Title = normalized.Title,
            HeaderName = normalized.HeaderName,
            HeaderPrefix = normalized.HeaderPrefix,
            AllowedTools = normalized.AllowedTools,
            ConfirmationMode = normalized.ConfirmationMode,
            Connected = true,
            Tools = tools,
            LastError = null,
            ProbedAt = NowUtc(),
            Status = ConnectionStatus.Connected,
            AuthMethodId = string.IsNullOrEmpty(Snapshot.AuthMethodId) ? "none" : Snapshot.AuthMethodId,
            Detail = null,
        };
        await SaveServerStateAsync(connected, publishConnectionEvent: true).ConfigureAwait(false);
        return ToServerState(connected);
    }

    public Task<McpServerState> Configure(McpConnectionConfig config, CancellationToken ct = default)
        => ConnectServer(config, ct);

    public async Task DisconnectServer(CancellationToken ct = default)
    {
        await _session.DropAsync().ConfigureAwait(false);
        ClearConfirmationState();
        var disconnected = Snapshot with
        {
            Connected = false,
            Status = ConnectionStatus.Disconnected,
        };
        await SaveServerStateAsync(disconnected, publishConnectionEvent: false).ConfigureAwait(false);
    }

    public async Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!Snapshot.Connected)
        {
            throw new ToolExecutionException(tool, $"MCP server '{Snapshot.Title}' is not connected.");
        }

        if (!McpClientSession.IsToolAllowed(tool, Snapshot.AllowedTools))
        {
            throw new ToolExecutionException(tool, $"Tool '{tool}' was not found in MCP server '{Snapshot.Title}'.", isNotFound: true);
        }

        var descriptor = Snapshot.Tools.FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null && Snapshot.Tools.Length > 0)
        {
            throw new ToolExecutionException(tool, $"Tool '{tool}' was not found in MCP server '{Snapshot.Title}'.", isNotFound: true);
        }

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new ToolExecutionException(tool, "Tool arguments must be a JSON object.", isValidationError: true);
        }

        var argumentsString = args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : args.GetRawText();
        var result = await Run(tool, argumentsString, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            throw new ToolExecutionException(tool, result.Text);
        }

        return ToolResultJson.From(result.Text, _log);
    }

    public async Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (!Snapshot.Connected)
        {
            throw new InvalidOperationException("The MCP server is not connected; connect first.");
        }

        if (!McpClientSession.IsToolAllowed(tool, Snapshot.AllowedTools))
        {
            throw new ToolExecutionException(tool, $"Tool '{tool}' was not found in MCP server '{Snapshot.Title}'.", isNotFound: true);
        }

        var confirmationGate = await CheckConfirmationAsync(tool).ConfigureAwait(false);
        if (confirmationGate is not null)
        {
            return confirmationGate;
        }

        var endpoint = McpClientSession.ValidateEndpoint(Snapshot.Url);
        var clock = Stopwatch.StartNew();
        var result = Policies is { } policies
            ? await policies.Guard("mcp.tool.run", 1m, new Dictionary<string, string>
            {
                ["Tool"] = tool,
                ["Server"] = Snapshot.Title,
                ["Connection"] = Id.Key,
            }, cancellation => _session.CallToolAsync(endpoint, CreateClientAsync, tool, arguments, cancellation), ct).ConfigureAwait(false)
            : await _session.CallToolAsync(endpoint, CreateClientAsync, tool, arguments, ct).ConfigureAwait(false);

        await PublishAsync(new McpToolRan(tool, result.IsError, (int)clock.ElapsedMilliseconds)).ConfigureAwait(false);
        return result;
    }

    [On]
    public Task OnConfirmationResolved(ConfirmationResolved resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (_pendingByPlanId.Remove(resolved.PlanId, out var pending)
            && resolved is { Outcome: ConfirmationOutcome.Approved, Skipped: false, Approver.IsHuman: true })
        {
            if (pending.Mode == McpConfirmationMode.OncePerSession)
            {
                _approvedSessions.Add(pending.SessionKey);
            }
            else if (pending.Mode == McpConfirmationMode.EveryCall)
            {
                _approvedSingleCalls.Add((pending.SessionKey, pending.Tool));
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task OnConnectedAsync()
    {
        await _session.DropAsync().ConfigureAwait(false);
        ClearConfirmationState();

        // Extract only non-secret configuration from the sealed payload inside UseCredentialAsync; the token never leaves.
        var config = await UseCredentialAsync((credential, _) =>
        {
            var parsed = ParseConfigOnly(credential.Value, Snapshot, _log);
            return Task.FromResult(parsed);
        }, CancellationToken.None).ConfigureAwait(false);

        Uri endpoint;
        try
        {
            endpoint = McpClientSession.ValidateEndpoint(config.Url);
            ValidateHeaderName(config.HeaderName);
        }
        catch (ArgumentException error)
        {
            var title = config.Title ?? string.Empty;
            var failed = Snapshot with
            {
                Url = config.Url,
                Title = title,
                HeaderName = config.HeaderName,
                HeaderPrefix = config.HeaderPrefix,
                AllowedTools = config.AllowedTools,
                ConfirmationMode = config.ConfirmationMode,
                Connected = false,
                Tools = [],
                LastError = error.Message,
                ProbedAt = NowUtc(),
            };
            await Commit(failed, new McpServerChanged(ToServerState(failed))).ConfigureAwait(false);
            await MarkFailing(error.Message).ConfigureAwait(false);
            return;
        }

        var normalized = NormalizeConfig(config, endpoint);
        var pendingState = Snapshot with
        {
            Url = normalized.Url,
            Title = normalized.Title,
            HeaderName = normalized.HeaderName,
            HeaderPrefix = normalized.HeaderPrefix,
            AllowedTools = normalized.AllowedTools,
            ConfirmationMode = normalized.ConfirmationMode,
            LastError = null,
            ProbedAt = NowUtc(),
        };
        await SaveQuietly(pendingState).ConfigureAwait(false);

        try
        {
            var tools = await _session.ListToolsAsync(endpoint, CreateClientAsync, normalized.AllowedTools, CancellationToken.None).ConfigureAwait(false);
            var connected = Snapshot with
            {
                Url = normalized.Url,
                Title = normalized.Title,
                HeaderName = normalized.HeaderName,
                HeaderPrefix = normalized.HeaderPrefix,
                AllowedTools = normalized.AllowedTools,
                ConfirmationMode = normalized.ConfirmationMode,
                Connected = true,
                Tools = tools,
                LastError = null,
                ProbedAt = NowUtc(),
            };
            await Commit(connected, new ConnectionUpdated(Key.Connector, Key.Connection), new McpServerChanged(ToServerState(connected))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogSwallowed(ex, $"Failed connecting MCP server '{normalized.Url}'.");
            var failed = Snapshot with
            {
                Url = normalized.Url,
                Title = normalized.Title,
                HeaderName = normalized.HeaderName,
                HeaderPrefix = normalized.HeaderPrefix,
                AllowedTools = normalized.AllowedTools,
                ConfirmationMode = normalized.ConfirmationMode,
                Connected = false,
                Tools = [],
                LastError = ex.Message,
                ProbedAt = NowUtc(),
            };
            await Commit(failed, new McpServerChanged(ToServerState(failed))).ConfigureAwait(false);
            await MarkFailing(ex.Message).ConfigureAwait(false);
        }
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        var config = ParseConfigOnly(credential.Value, Snapshot, _log);
        if (string.IsNullOrWhiteSpace(config.Url)
            || !Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint)
            || !McpClientSession.IsAllowedEndpoint(endpoint))
        {
            return ConnectionProbeResult.Failing("Invalid MCP server URL. Absolute HTTP/HTTPS URL required.");
        }

        var normalized = NormalizeConfig(config, endpoint);
        try
        {
            var secret = ExtractToken(credential.Value);
            var headers = McpClientSession.BuildHeaders(normalized.HeaderName, normalized.HeaderPrefix, secret);
            var tools = await _session.ProbeAsync(endpoint, headers, normalized.AllowedTools, ct).ConfigureAwait(false);
            return ConnectionProbeResult.Connected($"Connected to {normalized.Title} ({tools.Length} tools).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogSwallowed(ex, $"Probing MCP server '{normalized.Url}' failed.");
            return ConnectionProbeResult.Failing($"Probe failed: {ex.Message}");
        }
    }

    protected override async Task OnDisconnectedAsync()
    {
        await _session.DropAsync().ConfigureAwait(false);
        ClearConfirmationState();
        var disconnected = Snapshot with { Connected = false, Tools = [] };
        await Commit(disconnected, new ConnectionUpdated(Key.Connector, Key.Connection), new McpServerChanged(ToServerState(disconnected))).ConfigureAwait(false);
    }

    protected override async Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        await _session.DropAsync().ConfigureAwait(false);
        await base.OnDeactivate(cause, cancellationToken).ConfigureAwait(false);
    }

    private void ClearConfirmationState()
    {
        _approvedSessions.Clear();
        _approvedSingleCalls.Clear();
        _pendingByPlanId.Clear();
    }

    private Task<McpClient> CreateClientAsync(Uri endpoint, CancellationToken ct)
    {
        if (Snapshot.Secrets.Count == 0)
        {
            return _session.CreateClientAsync(endpoint, headers: null, ct);
        }

        return UseCredentialAsync((credential, token) =>
        {
            var secret = ExtractToken(credential.Value);
            var headers = McpClientSession.BuildHeaders(Snapshot.HeaderName, Snapshot.HeaderPrefix, secret);
            return _session.CreateClientAsync(endpoint, headers, token);
        }, ct);
    }

    private async Task<ToolResult?> CheckConfirmationAsync(string tool)
    {
        var mode = Snapshot.ConfirmationMode;
        if (mode == McpConfirmationMode.None)
        {
            return null;
        }

        var caller = Context?.Caller;
        if (caller is { Direct: true, Conversation: null, Actor: Actor.Person })
        {
            return null;
        }

        var sessionKey = caller?.Conversation ?? caller?.Actor.ToString() ?? "default";
        if (mode == McpConfirmationMode.OncePerSession && _approvedSessions.Contains(sessionKey))
        {
            return null;
        }

        if (mode == McpConfirmationMode.EveryCall && _approvedSingleCalls.Remove((sessionKey, tool)))
        {
            return null;
        }

        var confirmations = Confirmations;
        if (confirmations is null)
        {
            return new ToolResult(true, $"Confirmation is required before running '{tool}' on '{Snapshot.Title}', but confirmations are unavailable.", []);
        }

        foreach (var (planId, pending) in _pendingByPlanId.ToArray())
        {
            var matches = pending.Mode == mode
                && string.Equals(pending.SessionKey, sessionKey, StringComparison.Ordinal)
                && (mode == McpConfirmationMode.OncePerSession || string.Equals(pending.Tool, tool, StringComparison.Ordinal));
            if (!matches)
            {
                continue;
            }

            var outcome = await confirmations.Outcome(planId).ConfigureAwait(false);
            if (outcome is { Approved: true, Skipped: false, Approver.IsHuman: true })
            {
                _pendingByPlanId.Remove(planId);
                if (mode == McpConfirmationMode.OncePerSession)
                {
                    _approvedSessions.Add(sessionKey);
                }

                return null;
            }

            if (outcome is not null)
            {
                _pendingByPlanId.Remove(planId);
                continue;
            }

            return new ToolResult(true, $"Confirmation required ({planId}) before calling '{tool}' on '{Snapshot.Title}'. Ask the user to approve confirmation '{planId}' and retry.", []);
        }

        var newPlanId = $"mcp:{Id.Key}:{tool}:{Guid.NewGuid():N}";
        var plan = new ConfirmationPlan(
            Id: newPlanId,
            Action: ConfirmationAction.Other,
            Destructive: false,
            Title: $"Allow '{Snapshot.Title}' tool '{tool}'?",
            Body: mode == McpConfirmationMode.OncePerSession
                ? $"Approve tool calls to MCP server '{Snapshot.Title}' for this session."
                : $"Approve calling tool '{tool}' on MCP server '{Snapshot.Title}'.",
            ApproveLabel: "Allow",
            DenyLabel: "Deny",
            TargetLabel: Snapshot.Title)
        {
            RequiresPerson = true,
            Kind = "mcp.tool.run",
            Subject = $"{Id.Key}/{tool}",
        };

        var result = await confirmations.Request(plan).ConfigureAwait(false);
        if (result is { Approved: true, Skipped: false, Approver.IsHuman: true })
        {
            if (mode == McpConfirmationMode.OncePerSession)
            {
                _approvedSessions.Add(sessionKey);
            }

            return null;
        }

        if (result is null)
        {
            _pendingByPlanId[newPlanId] = new PendingConfirmation(sessionKey, tool, mode);
            return new ToolResult(true, $"Confirmation required ({newPlanId}) before calling '{tool}' on '{Snapshot.Title}'. Ask the user to approve confirmation '{newPlanId}' and retry.", []);
        }

        return new ToolResult(true, $"Tool '{tool}' on '{Snapshot.Title}' was not approved ({result.Outcome}).", []);
    }

    private async Task SaveServerStateAsync(McpToolConnectionState next, bool publishConnectionEvent)
    {
        var serverChanged = new McpServerChanged(ToServerState(next));
        if (publishConnectionEvent && TryParseAddress(out var connectorId, out var connectionId))
        {
            try
            {
                await Record(next, new ConnectionConnected(connectorId, connectionId, next.AuthMethodId, next.Account, next.ProbedAt)).ConfigureAwait(false);
                await PublishAsync(serverChanged).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log.LogSwallowed(error, "Saving MCP server state without connection catalog registration.");
            }
        }

        await Commit(next, serverChanged).ConfigureAwait(false);
    }

    private bool TryParseAddress(out string connectorId, out string connectionId)
    {
        var segments = Id.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && string.Equals(segments[0], "connection", StringComparison.Ordinal))
        {
            connectorId = segments[1];
            connectionId = string.Join('/', segments.Skip(2));
            return string.Equals(connectorId, Id.Type, StringComparison.Ordinal);
        }

        connectorId = string.Empty;
        connectionId = string.Empty;
        return false;
    }

    private DateTimeOffset NowUtc() => Context?.Time.GetUtcNow() ?? default;

    private static McpServerState ToServerState(McpToolConnectionState state) => new(
        state.Title,
        state.Url,
        state.Connected,
        state.Tools.ToArray(),
        state.LastError,
        state.ProbedAt,
        state.HeaderName,
        state.HeaderPrefix,
        state.AllowedTools is not null ? [.. state.AllowedTools] : null,
        state.ConfirmationMode);

    private static NormalizedConfig NormalizeConfig(McpConnectionConfig config, Uri endpoint)
    {
        var title = string.IsNullOrWhiteSpace(config.Title)
            ? (!string.IsNullOrWhiteSpace(endpoint.Host) ? endpoint.Host : "MCP Server")
            : config.Title.Trim();
        var headerName = string.IsNullOrWhiteSpace(config.HeaderName) ? null : config.HeaderName.Trim();
        var allowedTools = config.AllowedTools?.Where(static t => !string.IsNullOrWhiteSpace(t)).Select(static t => t.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return new NormalizedConfig(
            endpoint.ToString(),
            title,
            headerName,
            config.HeaderPrefix,
            allowedTools,
            config.ConfirmationMode);
    }

    private static void ValidateHeaderName(string? headerName)
    {
        if (!string.IsNullOrWhiteSpace(headerName)
            && headerName.Trim().Any(static c => char.IsWhiteSpace(c) || c == ':' || c == '\r' || c == '\n'))
        {
            throw new ArgumentException($"Header name '{headerName}' is not a valid HTTP header name.", nameof(headerName));
        }
    }

    private static McpConnectionConfig ParseConfigOnly(string? raw, McpToolConnectionState existing, ILogger log)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var url = ReadString(root, "url") ?? existing.Url;
                var title = ReadString(root, "title") ?? (string.IsNullOrWhiteSpace(existing.Title) ? null : existing.Title);
                var header = ReadString(root, "header") ?? ReadString(root, "headerName") ?? existing.HeaderName;
                var prefix = ReadString(root, "prefix") ?? ReadString(root, "headerPrefix") ?? existing.HeaderPrefix;
                var allowed = ReadStringArray(root, "allowedTools") ?? existing.AllowedTools;
                var mode = ReadConfirmationMode(root, "confirmationMode") ?? existing.ConfirmationMode;
                return new McpConnectionConfig(url, title, header, prefix, allowed, mode);
            }
            catch (JsonException error)
            {
                log.LogSwallowed(error, "Falling back to raw MCP connection string as URL.");
            }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return new McpConnectionConfig(
                trimmed,
                string.IsNullOrWhiteSpace(existing.Title) ? null : existing.Title,
                existing.HeaderName,
                existing.HeaderPrefix,
                existing.AllowedTools,
                existing.ConfirmationMode);
        }

        return new McpConnectionConfig(
            existing.Url,
            string.IsNullOrWhiteSpace(existing.Title) ? null : existing.Title,
            existing.HeaderName,
            existing.HeaderPrefix,
            existing.AllowedTools,
            existing.ConfirmationMode);
    }

    private static string? ExtractToken(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                return ReadString(root, "token");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A bare URL is keyless; any other non-URL string is treated as a raw token.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return null;
        }

        return trimmed;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static string[]? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                list.Add(value);
            }
        }

        return [.. list];
    }

    private static McpConfirmationMode? ReadConfirmationMode(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.String && Enum.TryParse<McpConfirmationMode>(prop.GetString(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num) && Enum.IsDefined(typeof(McpConfirmationMode), num))
        {
            return (McpConfirmationMode)num;
        }

        return null;
    }

    private sealed record NormalizedConfig(
        string Url,
        string Title,
        string? HeaderName,
        string? HeaderPrefix,
        string[]? AllowedTools,
        McpConfirmationMode ConfirmationMode);
}
