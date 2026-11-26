using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.UI;

namespace IntoChat.Muncly.Screens;

public sealed record MunclyAppState(
    string BaseUrl,
    string Path,
    bool IsConfiguredEndpoint,
    bool NavigationLocked,
    int ReloadRevision,
    string LastTransition,
    string Status)
{
    public const string DefaultBaseUrl = "https://crm.munc.ly";
    public const string ConfiguredUrlKey = "IntoChatOS:Modules:muncly:Options:BaseUrl";
    public const string IntegrationUrlKey = "IntoChatOS:Integrations:muncly:BaseUrl";
    public const string EnvUrlKey = "MUNCLY_CRM_URL";

    public MunclyAppState() : this(
        BaseUrl: DefaultBaseUrl,
        Path: "/",
        IsConfiguredEndpoint: false,
        NavigationLocked: false,
        ReloadRevision: 0,
        LastTransition: ViewTransition.None,
        Status: "Muncly CRM")
    { }

    [Invariant("BaseUrl is always a well-formed absolute https URL (or loopback http) without credentials."), JsonIgnore]
    public bool ValidBaseUrlInvariant => IsValidEndpoint(BaseUrl);

    [Invariant("Path always starts with '/' and never escapes the CRM origin."), JsonIgnore]
    public bool ValidPathInvariant => !string.IsNullOrEmpty(Path) && Path.StartsWith('/') && !Path.StartsWith("//", StringComparison.Ordinal);

    [Invariant("ReloadRevision stays within bounded toggle range [0, 1]."), JsonIgnore]
    public bool BoundedReloadInvariant => ReloadRevision is 0 or 1;

    [Invariant("LastTransition is always a canonical ViewTransition token."), JsonIgnore]
    public bool ValidTransitionInvariant => ViewTransition.IsValid(LastTransition);

    [JsonIgnore]
    public string CurrentUrl
    {
        get
        {
            var root = NormalizeBaseUrlOrFallback(BaseUrl);
            var cleanPath = NormalizePath(Path);
            var target = cleanPath == "/" ? root : $"{root}{cleanPath}";
            if (ReloadRevision == 0)
            {
                return target;
            }

            var separator = target.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{target}{separator}reload={ReloadRevision}";
        }
    }

    public static Rule ValidBaseUrlRule(string? baseUrl) => Decision.Rule(
        "invalid_crm_url",
        baseUrl,
        static raw => !IsValidEndpoint(raw),
        "Muncly CRM URL must be an absolute https URL (http allowed only for loopback) without credentials.");

    public Decision<MunclyAppState> ConnectEndpoint(string? baseUrl)
    {
        if (Decision.Check("connect", ValidBaseUrlRule(baseUrl)) is { } refusal)
        {
            return refusal;
        }

        var normalized = NormalizeBaseUrlOrFallback(baseUrl);
        return Decision.Accept(this with
        {
            BaseUrl = normalized,
            IsConfiguredEndpoint = true,
            ReloadRevision = 0,
            Status = $"Muncly CRM · {new Uri(normalized).Host}",
        });
    }

    public Decision<MunclyAppState> OpenPath(string? path, string? objectName = null, string? recordId = null, string? baseUrl = null)
    {
        var current = this;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var connected = current.ConnectEndpoint(baseUrl);
            if (!connected.IsAccepted)
            {
                return connected;
            }
            current = connected.Next!;
        }

        var targetPath = ResolvePath(path, objectName, recordId);
        return Decision.Accept(current with
        {
            Path = targetPath,
            ReloadRevision = 0,
            Status = targetPath == "/" ? "Muncly CRM" : $"Muncly CRM · {targetPath}",
        });
    }

    public Decision<MunclyAppState> Reload()
        => Decision.Accept(this with
        {
            ReloadRevision = ReloadRevision == 0 ? 1 : 0,
            Status = "Muncly CRM · Reloaded",
        });

    public Decision<MunclyAppState> ApplyNavigation(BrowserNavArgs args, string transition)
    {
        ArgumentNullException.ThrowIfNull(args);
        var normalizedTransition = ViewTransition.IsValid(transition) ? transition : ViewTransition.None;
        var next = this with { LastTransition = normalizedTransition };
        if (!string.IsNullOrWhiteSpace(args.BaseUrl))
        {
            var connected = next.ConnectEndpoint(args.BaseUrl);
            if (!connected.IsAccepted)
            {
                return connected;
            }
            next = connected.Next!;
        }

        if (!string.IsNullOrWhiteSpace(args.Section))
        {
            next = next with { Path = NormalizePath(args.Section) };
        }

        return Decision.Accept(next);
    }

    public Rule CanDepartRule(NavigationDeparture departure)
        => Decision.Rule(
            "navigation_locked",
            NavigationLocked && !departure.Force,
            static locked => locked,
            "Muncly CRM navigation is currently locked.");

    public static bool IsValidEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    public static string NormalizeBaseUrlOrFallback(string? raw)
    {
        if (!IsValidEndpoint(raw))
        {
            return DefaultBaseUrl;
        }

        var uri = new Uri(raw!.Trim(), UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "/";
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        while (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string ResolvePath(string? path, string? objectName, string? recordId)
    {
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            var obj = Uri.EscapeDataString(objectName.Trim());
            return string.IsNullOrWhiteSpace(recordId)
                ? $"/objects/{obj}"
                : $"/objects/{obj}/{Uri.EscapeDataString(recordId.Trim())}";
        }

        return NormalizePath(path);
    }
}
