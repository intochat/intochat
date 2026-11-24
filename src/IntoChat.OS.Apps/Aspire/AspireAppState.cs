using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Aspire;

/// <summary>
/// Pure DDD Aggregate state and route/endpoint transition guards for <see cref="AspireApp"/> (<c>app.aspire</c>).
/// Enforces valid <c>http</c>/<c>https</c> Aspire Dashboard endpoints, supported telemetry sections, and safe W3C trace deep-links.
/// </summary>
public sealed record AspireAppState(
    string BaseUrl,
    string Section,
    string? TraceId,
    bool IsConfiguredEndpoint,
    bool ShowConnectBar,
    bool NavigationLocked,
    int ReloadRevision,
    string LastTransition,
    string Status)
{
    public const string DefaultBaseUrl = "http://localhost:18888";
    public const string ConfiguredUrlKey = "IntoChatOS:AspireDashboardUrl";
    public const string DashboardUrlEnvKey = "ASPIRE_DASHBOARD_URL";
    public const string DotnetDashboardUrlsKey = "DOTNET_DASHBOARD_URLS";

    public static readonly string[] EndpointKeys = [ConfiguredUrlKey, DashboardUrlEnvKey, DotnetDashboardUrlsKey];

    public static readonly string[] SupportedSections =
    [
        "resources",
        "traces",
        "structuredlogs",
        "consolelogs",
        "metrics",
    ];

    public AspireAppState() : this(
        BaseUrl: DefaultBaseUrl,
        Section: "resources",
        TraceId: null,
        IsConfiguredEndpoint: false,
        ShowConnectBar: false,
        NavigationLocked: false,
        ReloadRevision: 0,
        LastTransition: ViewTransition.None,
        Status: "Aspire Dashboard · Resources")
    { }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("BaseUrl is always a well-formed absolute http or https URL without credentials."), JsonIgnore]
    public bool ValidBaseUrlInvariant => IsValidHttpEndpoint(BaseUrl);

    [Invariant("Section is always one of resources, traces, structuredlogs, consolelogs, or metrics."), JsonIgnore]
    public bool ValidSectionInvariant => SupportedSections.Contains(Section, StringComparer.Ordinal);

    [Invariant("TraceId, when set, is a safe trace identifier and only active on the traces section."), JsonIgnore]
    public bool ValidTraceDeepLinkInvariant =>
        TraceId is null || (string.Equals(Section, "traces", StringComparison.Ordinal) && IsValidTraceId(TraceId));

    [Invariant("ReloadRevision stays within bounded toggle range [0, 1]."), JsonIgnore]
    public bool BoundedReloadInvariant => ReloadRevision is 0 or 1;

    [Invariant("LastTransition is always a canonical ViewTransition token."), JsonIgnore]
    public bool ValidTransitionInvariant => ViewTransition.IsValid(LastTransition);

    // ── Computed Presentation Projections ────────────────────────────────────

    [JsonIgnore]
    public string CurrentUrl
    {
        get
        {
            var root = NormalizeBaseUrlOrFallback(BaseUrl);
            var route = string.Equals(Section, "traces", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(TraceId)
                ? $"traces/detail/{Uri.EscapeDataString(TraceId)}"
                : Section;
            return ReloadRevision == 0 ? $"{root}/{route}" : $"{root}/{route}?reload={ReloadRevision}";
        }
    }

    [JsonIgnore]
    public string EndpointBadge
    {
        get
        {
            var authority = TryExtractAuthority(BaseUrl) ?? BaseUrl;
            return IsConfiguredEndpoint ? $"Live · {authority}" : $"Endpoint · {authority}";
        }
    }

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedSectionRule(string? section) => Decision.Rule(
        "invalid_section",
        section,
        static raw => ResolveCanonicalSection(raw) is null,
        "Aspire section must be resources, traces, structuredlogs, consolelogs, or metrics.");

    public static Rule ValidBaseUrlRule(string? baseUrl) => Decision.Rule(
        "invalid_dashboard_url",
        baseUrl,
        static raw => !TryNormalizeBaseUrl(raw, out _),
        "Aspire Dashboard URL must be a well-formed http:// or https:// endpoint.");

    public static Rule ValidTraceIdRule(string? traceId) => Decision.Rule(
        "invalid_trace_id",
        traceId,
        static raw => !IsValidTraceId(raw),
        "TraceId must be 8–64 ASCII hexadecimal or token characters without path or query delimiters.");

    public Rule CanDepartRule(NavigationDeparture departure) => Decision.Rule(
        "navigation_locked",
        (Locked: NavigationLocked, departure.Force),
        static x => x.Locked && !x.Force,
        "Aspire live telemetry view is pinned; unlock navigation or force departure.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public Decision<AspireAppState> SelectSection(
        [Sample("resources", "traces", "structuredlogs", "consolelogs", "metrics", "invalid-section")] string? section)
    {
        if (Decision.Check("select-section", SupportedSectionRule(section)) is { } refused)
        {
            return refused;
        }

        var canonical = ResolveCanonicalSection(section)!;
        var nextTraceId = string.Equals(canonical, "traces", StringComparison.Ordinal) ? TraceId : null;
        return Decision.Accept(this with
        {
            Section = canonical,
            TraceId = nextTraceId,
            Status = $"Aspire Dashboard · {FormatSectionLabel(canonical)}",
        });
    }

    public Decision<AspireAppState> FocusTrace(
        [Sample("4bf92f3577b34da6a3ce929d0e0e4736", "bad/trace?id")] string? traceId)
    {
        if (Decision.Check("focus-trace", ValidTraceIdRule(traceId)) is { } refused)
        {
            return refused;
        }

        var clean = traceId!.Trim();
        return Decision.Accept(this with
        {
            Section = "traces",
            TraceId = clean,
            Status = $"Aspire Dashboard · Trace {ShortTrace(clean)}",
        });
    }

    public Decision<AspireAppState> ConnectEndpoint(
        [Sample("http://localhost:18888", "https://aspire.internal:18888", "ftp://invalid.example", "not-a-url")] string? baseUrl)
    {
        if (Decision.Check("connect", ValidBaseUrlRule(baseUrl)) is { } refused)
        {
            return refused;
        }

        TryNormalizeBaseUrl(baseUrl, out var normalized);
        return Decision.Accept(this with
        {
            BaseUrl = normalized!,
            IsConfiguredEndpoint = true,
            ShowConnectBar = false,
            Status = $"Connected to Aspire Dashboard ({TryExtractAuthority(normalized!) ?? normalized!})",
        });
    }

    public AspireAppState Reload() => this with
    {
        ReloadRevision = ReloadRevision == 0 ? 1 : 0,
        Status = $"Reloaded Aspire Dashboard · {FormatSectionLabel(Section)}",
    };

    [IgnoreExplore]
    public AspireAppState ToggleConnectBar() => this with
    {
        ShowConnectBar = !ShowConnectBar,
    };

    [IgnoreExplore]
    public AspireAppState SetNavigationLock(bool locked) => this with
    {
        NavigationLocked = locked,
    };

    [IgnoreExplore]
    public Decision<AspireAppState> ApplyNavigation(BrowserNavArgs? args, string transition = ViewTransition.FadeThrough)
    {
        var normalizedTransition = ViewTransition.Normalize(transition);
        if (args is null)
        {
            return Decision.Accept(this with { LastTransition = normalizedTransition });
        }

        var rules = new List<Rule>();
        if (!string.IsNullOrWhiteSpace(args.BaseUrl))
        {
            rules.Add(ValidBaseUrlRule(args.BaseUrl));
        }

        string? routeSection = null;
        string? effectiveTraceId = null;

        if (!string.IsNullOrWhiteSpace(args.Section))
        {
            routeSection = args.Section;
            rules.Add(SupportedSectionRule(routeSection));
            if (!string.IsNullOrWhiteSpace(args.TraceId))
            {
                effectiveTraceId = args.TraceId.Trim();
                rules.Add(ValidTraceIdRule(effectiveTraceId));
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.TraceId))
        {
            ClassifyTraceOrFlowId(args.TraceId, out routeSection, out effectiveTraceId, rules);
        }
        else if (!string.IsNullOrWhiteSpace(args.Route))
        {
            ClassifyRouteOrInput(args.Route, out routeSection, out effectiveTraceId, rules);
        }

        if (rules.Count > 0 && Decision.Check("navigate-to", [.. rules]) is { } refused)
        {
            return refused;
        }

        var nextBaseUrl = BaseUrl;
        var nextConfigured = IsConfiguredEndpoint;
        if (!string.IsNullOrWhiteSpace(args.BaseUrl) && TryNormalizeBaseUrl(args.BaseUrl, out var parsedUrl))
        {
            nextBaseUrl = parsedUrl!;
            nextConfigured = true;
        }

        var nextSection = !string.IsNullOrWhiteSpace(effectiveTraceId)
            ? "traces"
            : (!string.IsNullOrWhiteSpace(routeSection) ? ResolveCanonicalSection(routeSection)! : Section);

        var nextTrace = string.Equals(nextSection, "traces", StringComparison.Ordinal)
            ? (!string.IsNullOrWhiteSpace(effectiveTraceId)
                ? effectiveTraceId!.Trim()
                : (string.IsNullOrWhiteSpace(routeSection) ? TraceId : null))
            : null;

        var detail = nextTrace is not null ? $"Trace {ShortTrace(nextTrace)}" : FormatSectionLabel(nextSection);
        return Decision.Accept(this with
        {
            BaseUrl = nextBaseUrl,
            IsConfiguredEndpoint = nextConfigured,
            ShowConnectBar = false,
            Section = nextSection,
            TraceId = nextTrace,
            LastTransition = normalizedTransition,
            Status = $"Aspire Dashboard · {detail}",
        });
    }

    // ── Pure Validation & Formatting Helpers ─────────────────────────────────

    public static string? ResolveCanonicalSection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var slug = raw.Trim().Trim('/').ToLowerInvariant();
        return slug switch
        {
            "resources" or "resource" => "resources",
            "traces" or "trace" => "traces",
            "structuredlogs" or "structured-logs" or "logs" => "structuredlogs",
            "consolelogs" or "console-logs" or "console" => "consolelogs",
            "metrics" or "metric" => "metrics",
            _ => null,
        };
    }

    public static string FormatSectionLabel(string section) => section switch
    {
        "resources" => "Resources",
        "traces" => "Traces",
        "structuredlogs" => "Structured Logs",
        "consolelogs" => "Console",
        "metrics" => "Metrics",
        _ => "Resources",
    };

    public static string ShortTrace(string traceId)
        => string.IsNullOrWhiteSpace(traceId) ? string.Empty : (traceId.Length > 12 ? traceId[..12] : traceId);

    public static bool IsValidHttpEndpoint(string? candidate)
        => TryNormalizeBaseUrl(candidate, out _);

    public static bool TryNormalizeBaseUrl(string? candidate, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        foreach (var part in candidate.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                continue;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            normalized = string.IsNullOrEmpty(path)
                ? uri.GetLeftPart(UriPartial.Authority)
                : $"{uri.GetLeftPart(UriPartial.Authority)}{path}";
            return true;
        }

        return false;
    }

    public static bool IsValidTraceId(string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return false;
        }

        var trimmed = traceId.Trim();
        if (trimmed.Length is < 8 or > 64)
        {
            return false;
        }

        var hasNonZero = false;
        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                return false;
            }

            if (ch != '0' && ch != '-')
            {
                hasNonZero = true;
            }
        }

        return hasNonZero;
    }

    public static bool IsHexTraceId(string? traceId)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return false;
        }

        var trimmed = traceId.Trim();
        if (trimmed.Length is < 8 or > 64)
        {
            return false;
        }

        var hasNonZero = false;
        foreach (var ch in trimmed)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }

            if (ch != '0')
            {
                hasNonZero = true;
            }
        }

        return hasNonZero;
    }

    private static string NormalizeBaseUrlOrFallback(string? raw)
        => TryNormalizeBaseUrl(raw, out var normalized) ? normalized! : DefaultBaseUrl;

    private static string? TryExtractAuthority(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;

    private static void ClassifyRouteOrInput(
        string rawRoute,
        out string? routeSection,
        out string? effectiveTraceId,
        List<Rule> rules)
    {
        routeSection = null;
        effectiveTraceId = null;

        var trimmed = rawRoute.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (ResolveCanonicalSection(trimmed) is { } canonical)
        {
            routeSection = canonical;
            return;
        }

        var stripped = trimmed.TrimStart('#');
        if (stripped.StartsWith('/'))
        {
            stripped = stripped[1..];
        }

        const string detailPrefix = "traces/detail/";
        if (stripped.StartsWith(detailPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var tail = stripped[detailPrefix.Length..].Trim('/');
            ClassifyTraceOrFlowId(tail, out routeSection, out effectiveTraceId, rules);
            return;
        }

        if (stripped.StartsWith("trace/", StringComparison.OrdinalIgnoreCase)
            || stripped.StartsWith("trace:", StringComparison.OrdinalIgnoreCase))
        {
            var tail = stripped[6..].Trim();
            ClassifyTraceOrFlowId(tail, out routeSection, out effectiveTraceId, rules);
            return;
        }

        ClassifyTraceOrFlowId(trimmed, out routeSection, out effectiveTraceId, rules);
    }

    private static void ClassifyTraceOrFlowId(
        string rawCandidate,
        out string? routeSection,
        out string? effectiveTraceId,
        List<Rule> rules)
    {
        routeSection = null;
        effectiveTraceId = null;

        var clean = rawCandidate.Trim();
        if (clean.StartsWith("trace:", StringComparison.OrdinalIgnoreCase)
            || clean.StartsWith("trace/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[6..].Trim();
        }

        if (IsHexTraceId(clean))
        {
            routeSection = "traces";
            effectiveTraceId = clean;
            return;
        }

        if (IsSafeFlowIdentifier(clean, out var trailingKey))
        {
            routeSection = "traces";
            effectiveTraceId = trailingKey is not null && IsHexTraceId(trailingKey) ? trailingKey : null;
            return;
        }

        rules.Add(ValidTraceIdRule(rawCandidate));
    }

    private static bool IsSafeFlowIdentifier(string? candidate, out string? trailingKey)
    {
        trailingKey = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (trimmed.Length is < 2 or > 128
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains(':', StringComparison.Ordinal)
            || trimmed.Contains('?', StringComparison.Ordinal)
            || trimmed.Contains('#', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var segments = trimmed.Split('/');
            if (segments.Length != 3
                || segments.Any(string.IsNullOrWhiteSpace)
                || !segments[1].StartsWith("flow.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var seg in segments)
            {
                if (!seg.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
                {
                    return false;
                }
            }

            trailingKey = segments[2];
            return true;
        }

        var hasLetterOrDigit = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                hasLetterOrDigit = true;
                continue;
            }

            if (ch is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return hasLetterOrDigit;
    }

    public override string ToString()
        => TraceId is { Length: > 0 } tid ? $"{Section}:{ShortTrace(tid)}" : Section;
}
