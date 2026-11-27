using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;

namespace IntoChat.OS.Kernel.Platform;

// A gRPC message carries its brain where an HTTP route carries it in the path: the same id rule,
// the same membership decision, then the brain stamped into the caller for BrainScope.CurrentId().
internal static class BrainCall
{
    private static readonly string[] ConnectorContractNamespaces =
    [
        "DigitalBrain.Capabilities.Connections.",
        "IntoChat.OS.Modules.Identity.Contracts.Integrations.",
        "IntoChat.OS.Modules.GitHub.",
        "IntoChat.OS.Modules.Google.",
        "IntoChat.OS.Modules.Salesforce.",
        "IntoChat.OS.Modules.Azure.",
        "IntoChat.OS.Modules.Postgres.",
        "IntoChat.OS.Modules.ClickHouse.",
        "IntoChat.OS.Modules.Windows.",
        "IntoChat.OS.Modules.Mcp.Contracts.Mcp.",
    ];

    private static readonly HashSet<string> ConnectorContractNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IConnection",
        "IConnectionInbound",
        "IConnectionCatalog",
        "IConnectionRequests",
        "ICapabilityConnections",
        "IConnectorOffers",
        "IIntegration",
        "IRepository",
        "IGitHubInbound",
        "IGmail",
        "IGmailInbound",
        "IDrive",
        "ISalesforce",
        "IAzure",
        "IPulumi",
        "IPostgresServer",
        "IPostgresTableHost",
        "IClickHouse",
        "IWindows",
        "IWindowsFolderGateway",
        "IMcpToolConnection",
        "connection",
        "connections",
        "connection-catalog",
        "connection-requests",
        "integration",
        "integrations",
        "mcp.tool",
        "github",
        "gmail",
        "drive",
        "salesforce",
        "azure",
        "pulumi",
        "postgres",
        "clickhouse",
        "windows",
    };

    private static readonly HashSet<string> AutomationContractNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IReminder",
        "IReminders",
        "ITimer",
        "ITimers",
        "ISchedule",
        "ISelfImprovementLoop",
        "SelfImprovementLoop",
        "reminder",
        "timer",
        "schedule",
        "loop.self-improvement",
    };

    private static readonly HashSet<string> DeviceContractNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IDeviceEnrollment",
        "IDeviceEnrollmentRedeemer",
        "IDeviceLink",
        "IDevice",
        "IDeviceChatRoute",
        "IDeviceInstallReports",
        "devices.enrollment",
        "devices.link",
        "devices.device",
    };

    private static readonly HashSet<string> SecretContractNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IVault",
        "IOsVault",
        "ISecretVault",
        "ISecrets",
        "ISecretVaultPort",
        "ISecretValueSource",
        "IBrainKeys",
        "IBrainSecretSealer",
        "ISecretSealer",
        "IOAuthTokenExchange",
        "identity.vault",
        "identity.brain-keys",
        "vault",
        "secrets",
        "oauth-exchange",
    };

    private static readonly HashSet<string> GuestDeniedAdminContracts = new(StringComparer.OrdinalIgnoreCase)
    {
        "IGrantsNeuron",
        "IDirectoryStore",
        "IHandleDirectory",
        "ICodeIndex",
        "ICodeKey",
        "identity.grants",
        "identity.directory",
        "identity.handles",
        "identity.codes",
        "identity.code-key",
    };

    // The edge's caller again, in the brain the message names, once the membership decision admitted it. The caller enters it:
    // an ambient entered in an async helper does not flow back.
    public static async Task<Caller> Enter(ServerCallContext context, string brainId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new RpcException(new Status(StatusCode.InvalidArgument, "brain_id is not a valid brain id.")); }
        var http = context.GetHttpContext();
        var edge = http.RequestServices.GetRequiredService<EdgeAuthority>();
        var brain = new BrainId(brainId);
        var asApp = ActingAs(http);
        var caller = HttpCaller.Current(http) switch
        {
            { Actor: Actor.Person person } current when asApp is not null => edge.App(asApp, person, brain, current.Conversation),
            { Actor: Actor.Person person } current => edge.Person(person.Principal, person.Account, brain, current.Conversation),
            { Actor: Actor.App { Key: GuestSurface.AppId } app } current => edge.Guest(app.For.Principal, app.For.Account, brain, current.Conversation),
            null => throw new RpcException(new Status(StatusCode.Unauthenticated, "The call carries no caller.")),
            _ => throw new RpcException(new Status(StatusCode.PermissionDenied, "Only a person's session reaches a brain over this channel.")),
        };
        if (await BrainAccessFilter.Decide(http, brainId) is { } denied)
        {
            var code = denied is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized } ? StatusCode.Unauthenticated : StatusCode.PermissionDenied;
            throw new RpcException(new Status(code, "This brain is not yours."));
        }
        return caller;
    }

    // Enforces IBrainCapabilityPolicy on gRPC neuron calls so unauthenticated Day-0 ephemeral sessions cannot bypass
    // HTTP capability gates by invoking connector, background automation, device enrollment, or secret neurons directly.
    internal static async Task EnforceCapabilityAsync(
        IServiceProvider services,
        Caller caller,
        string brainId,
        string? contract,
        string? key,
        NeuronSurface? surface,
        CancellationToken ct)
    {
        var isGuest = GuestSurface.Is(caller);
        var isEphemeral = services.GetService<IBrainResidencyRegistry>()?.TryGetEphemeral(brainId, out _) == true;

        var match = ClassifyCapability(contract, key, resolvedContract: null);
        Type? resolved = null;
        if (match is null && surface is not null && (isGuest || isEphemeral))
        {
            resolved = await surface.TryResolveContract(contract ?? string.Empty, ct).ConfigureAwait(false);
            match = ClassifyCapability(contract, key, resolved);
        }

        if (isGuest && IsAdminContract(contract, key, resolved))
        {
            throw new UnauthorizedAccessException("An ephemeral guest session cannot access administrative identity neurons.");
        }

        if (match is null)
        {
            return;
        }

        var policy = services.GetService<IBrainCapabilityPolicy>();
        if (policy is not null)
        {
            var decision = await policy.EvaluateAsync(brainId, match.Value.Capability, match.Value.Resource, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                throw new ClaimRequiredException(decision);
            }
        }
        else if (isGuest || isEphemeral)
        {
            throw new ClaimRequiredException(CapabilityDecision.RequireClaim(
                match.Value.Capability,
                "Sign in to claim your workspace to unlock this capability.",
                match.Value.Resource));
        }
    }

    internal static (BrainCapability Capability, string? Resource)? ClassifyCapability(
        string? contract,
        string? key,
        Type? resolvedContract = null)
    {
        var contractText = contract?.Trim() ?? string.Empty;
        var keyText = key?.Trim() ?? string.Empty;
        var shortName = ShortContractName(contractText);
        var resolvedFullName = resolvedContract?.FullName ?? string.Empty;
        var resolvedShortName = resolvedContract?.Name ?? string.Empty;

        // 1. Durable secrets & vault neurons
        if (SecretContractNames.Contains(contractText)
            || SecretContractNames.Contains(shortName)
            || SecretContractNames.Contains(resolvedShortName)
            || contractText.StartsWith("DigitalBrain.Capabilities.Secrets.", StringComparison.OrdinalIgnoreCase)
            || resolvedFullName.StartsWith("DigitalBrain.Capabilities.Secrets.", StringComparison.OrdinalIgnoreCase)
            || ContainsSegment(keyText, "identity.vault")
            || ContainsSegment(keyText, "identity.brain-keys")
            || ContainsSegment(keyText, "vault")
            || ContainsSegment(keyText, "secrets")
            || ContainsSegment(keyText, "oauth-exchange"))
        {
            return (BrainCapability.DurableSecrets, string.IsNullOrEmpty(shortName) ? keyText : shortName);
        }

        // 2. Device enrollment & device link neurons
        if (DeviceContractNames.Contains(contractText)
            || DeviceContractNames.Contains(shortName)
            || DeviceContractNames.Contains(resolvedShortName)
            || contractText.StartsWith("IntoChat.OS.Modules.Devices.", StringComparison.OrdinalIgnoreCase)
            || resolvedFullName.StartsWith("IntoChat.OS.Modules.Devices.", StringComparison.OrdinalIgnoreCase)
            || ContainsSegment(keyText, "device-enrollment")
            || ContainsSegment(keyText, "devices.enrollment")
            || ContainsSegment(keyText, "devices.link")
            || ContainsSegment(keyText, "devices.device"))
        {
            return (BrainCapability.DeviceEnrollment, string.IsNullOrEmpty(shortName) ? keyText : shortName);
        }

        // 3. Background automations, reminders, timers, schedules
        if (AutomationContractNames.Contains(contractText)
            || AutomationContractNames.Contains(shortName)
            || AutomationContractNames.Contains(resolvedShortName)
            || contractText.StartsWith("IntoChat.OS.Modules.Time.", StringComparison.OrdinalIgnoreCase)
            || resolvedFullName.StartsWith("IntoChat.OS.Modules.Time.", StringComparison.OrdinalIgnoreCase)
            || ContainsSegment(keyText, "reminder")
            || ContainsSegment(keyText, "timer")
            || ContainsSegment(keyText, "loop.self-improvement"))
        {
            return (BrainCapability.BackgroundAutomation, string.IsNullOrEmpty(shortName) ? keyText : shortName);
        }

        // 4. External connectors, connection neurons, and integrations
        if (ConnectionKeys.TryParse(keyText, out var parsedConn))
        {
            return (BrainCapability.ExternalConnector, parsedConn.ConnectorId);
        }

        if (resolvedContract is not null
            && (typeof(IConnection).IsAssignableFrom(resolvedContract)
                || typeof(IConnectionInbound).IsAssignableFrom(resolvedContract)
                || typeof(IConnectionCatalog).IsAssignableFrom(resolvedContract)
                || typeof(IConnectionRequests).IsAssignableFrom(resolvedContract)
                || typeof(IIntegration).IsAssignableFrom(resolvedContract)))
        {
            return (BrainCapability.ExternalConnector, resolvedShortName);
        }

        if (ConnectorContractNames.Contains(contractText)
            || ConnectorContractNames.Contains(shortName)
            || ConnectorContractNames.Contains(resolvedShortName)
            || StartsWithAny(contractText, ConnectorContractNamespaces)
            || StartsWithAny(resolvedFullName, ConnectorContractNamespaces)
            || ContainsSegment(keyText, "connection")
            || ContainsSegment(keyText, "connections")
            || ContainsSegment(keyText, "integrations")
            || ContainsSegment(keyText, "mcp.tool"))
        {
            return (BrainCapability.ExternalConnector, string.IsNullOrEmpty(shortName) ? keyText : shortName);
        }

        return null;
    }

    internal static bool IsAdminContract(string? contract, string? key, Type? resolvedContract = null)
    {
        var contractText = contract?.Trim() ?? string.Empty;
        var keyText = key?.Trim() ?? string.Empty;
        var shortName = ShortContractName(contractText);
        var resolvedShortName = resolvedContract?.Name ?? string.Empty;

        return GuestDeniedAdminContracts.Contains(contractText)
            || GuestDeniedAdminContracts.Contains(shortName)
            || GuestDeniedAdminContracts.Contains(resolvedShortName)
            || ContainsSegment(keyText, "identity.grants")
            || ContainsSegment(keyText, "identity.directory")
            || ContainsSegment(keyText, "identity.handles")
            || ContainsSegment(keyText, "identity.codes")
            || ContainsSegment(keyText, "identity.code-key");
    }

    private static string ShortContractName(string contract)
    {
        if (string.IsNullOrEmpty(contract)) { return string.Empty; }
        var dot = contract.LastIndexOf('.');
        return dot >= 0 && dot < contract.Length - 1 ? contract[(dot + 1)..] : contract;
    }

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        if (string.IsNullOrEmpty(value)) { return false; }
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    private static bool ContainsSegment(string key, string segment)
    {
        if (string.IsNullOrEmpty(key)) { return false; }
        var normalized = ConnectionKeys.Is(key) || key.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? (key.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal) ? key[ConnectionKeys.NeuronScheme.Length..] : key)
            : key;
        foreach (var part in normalized.Split('/'))
        {
            if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    // The edge app a proven person asked to act as (ActingApp), or null. A header naming anything else is refused rather than
    // ignored, so a caller never runs with more authority than it asked for.
    private static string? ActingAs(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(ActingApp.HeaderName, out var named)) { return null; }
        if (named.Count != 1 || !ActingApp.IsEdgeApp(named[0]))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"{ActingApp.HeaderName} may only name an edge app of the OS."));
        }
        return named[0];
    }

    // Error details ride in trailers until the status-details packages arrive with protocol v1 (W2 PR 5a):
    // the domain, a reason from the closed list, and per-reason metadata.
    internal const string ErrorDomain = "ui.intochat-os.io";
    internal const string DomainTrailer = "error-domain";
    internal const string ReasonTrailer = "error-reason";
    internal const string CurrentRevisionTrailer = "current-revision";

    // A caller's own mistakes come back with their reason; the host's failures stay in its logs.
    public static RpcException Translate(Exception error, ILogger? logger = null)
    {
        switch (error)
        {
            // Before ArgumentException and the default: both conflicts are the caller's to resolve, not host failures.
            case RevisionConflictException e:
                return Refused(StatusCode.Aborted, e.Message, "REVISION_CONFLICT", (CurrentRevisionTrailer, e.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case StaleActionException e: return Refused(StatusCode.FailedPrecondition, e.Message, "STALE_ACTION");
            case ClaimRequiredException e:
                return Refused(
                    StatusCode.FailedPrecondition,
                    e.Message,
                    e.Decision.Code,
                    ("requires-claim", "true"),
                    ("capability", e.Decision.Capability.ToString()),
                    ("claim-endpoint", "/identity/claim"));
            case BrainMemoryLimitExceededException e:
                return Refused(StatusCode.ResourceExhausted, e.Message, "EPHEMERAL_MEMORY_LIMIT", ("requires-claim", "true"), ("claim-endpoint", "/identity/claim"));
            case ArgumentException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            case UnauthorizedAccessException e: return new RpcException(new Status(StatusCode.PermissionDenied, e.Message));
            case KeyNotFoundException e: return new RpcException(new Status(StatusCode.NotFound, e.Message));
            case TypeLoadException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            default:
                logger?.LogError(error, "Neuron channel call failed");
                return new RpcException(new Status(StatusCode.Internal, "The neuron channel failed; see the host's logs."));
        }
    }

    private static RpcException Refused(StatusCode code, string message, string reason, params (string Key, string Value)[] details)
    {
        var trailers = new Metadata { { DomainTrailer, ErrorDomain }, { ReasonTrailer, reason } };
        foreach (var (key, value) in details) { trailers.Add(key, value); }
        return new RpcException(new Status(code, message), trailers, message);
    }
}
