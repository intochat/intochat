using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Orleans;

/// <summary>
/// Pure DDD Aggregate state and route/filter transition guards for <see cref="OrleansApp"/> (<c>app.orleans</c>).
/// Enforces supported Orleans Dashboard sections (<c>overview</c>, <c>grains</c>, <c>silos</c>, <c>reminders</c>),
/// safe grain-type filters, and co-hosted <c>/orleans</c> or <c>http</c>/<c>https</c> dashboard endpoints.
/// </summary>
public sealed record OrleansAppState(
    string DashboardBase,
    string Section,
    string? GrainFilter,
    bool NavigationLocked,
    int ReloadRevision,
    string LastTransition,
    string Status)
{
    public const string DefaultDashboardBase = "/orleans";

    public static readonly string[] SupportedSections =
    [
        "overview",
        "grains",
        "silos",
        "reminders",
    ];

    public OrleansAppState() : this(
        DashboardBase: DefaultDashboardBase,
        Section: "overview",
        GrainFilter: null,
        NavigationLocked: false,
        ReloadRevision: 0,
        LastTransition: ViewTransition.None,
        Status: "Orleans Grain Dashboard · Overview")
    { }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("DashboardBase is always the co-hosted /orleans path or a valid http/https endpoint."), JsonIgnore]
    public bool ValidDashboardBaseInvariant => IsValidDashboardBase(DashboardBase);

    [Invariant("Section is always one of overview, grains, silos, or reminders."), JsonIgnore]
    public bool ValidSectionInvariant => SupportedSections.Contains(Section, StringComparer.Ordinal);

    [Invariant("GrainFilter, when set, is a safe type token and only active on the grains view."), JsonIgnore]
    public bool ValidGrainFilterInvariant =>
        GrainFilter is null || (string.Equals(Section, "grains", StringComparison.Ordinal) && IsValidGrainFilter(GrainFilter));

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
            var baseTarget = NormalizeDashboardBaseOrFallback(DashboardBase);
            var query = ReloadRevision == 0 ? string.Empty : $"?reload={ReloadRevision}";
            var fragment = string.Equals(Section, "grains", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(GrainFilter)
                ? $"#/grains/{Uri.EscapeDataString(GrainFilter)}"
                : $"#/{Section}";
            return $"{baseTarget}{query}{fragment}";
        }
    }

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedSectionRule(string? section) => Decision.Rule(
        "invalid_section",
        section,
        static raw => ResolveCanonicalSection(raw) is null,
        "Orleans dashboard section must be overview, grains, silos, or reminders.");

    public static Rule ValidGrainFilterRule(string? grainFilter) => Decision.Rule(
        "invalid_grain_filter",
        grainFilter,
        static raw => !IsValidGrainFilter(raw),
        "GrainFilter must be 1–128 safe identifier characters without path, query, or script delimiters.");

    public static Rule ValidDashboardBaseRule(string? dashboardBase) => Decision.Rule(
        "invalid_dashboard_base",
        dashboardBase,
        static raw => !TryNormalizeDashboardBase(raw, out _),
        "Orleans dashboard base must be '/orleans' or a valid http:// / https:// endpoint.");

    public Rule CanDepartRule(NavigationDeparture departure) => Decision.Rule(
        "navigation_locked",
        (Locked: NavigationLocked, departure.Force),
        static x => x.Locked && !x.Force,
        "Orleans grain inspection view is pinned; unlock navigation or force departure.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public Decision<OrleansAppState> SelectSection(
        [Sample("overview", "grains", "silos", "reminders", "invalid-tab")] string? section)
    {
        if (Decision.Check("select-section", SupportedSectionRule(section)) is { } refused)
        {
            return refused;
        }

        var canonical = ResolveCanonicalSection(section)!;
        var nextFilter = string.Equals(canonical, "grains", StringComparison.Ordinal) ? GrainFilter : null;
        return Decision.Accept(this with
        {
            Section = canonical,
            GrainFilter = nextFilter,
            Status = $"Orleans Grain Dashboard · {FormatSectionLabel(canonical)}",
        });
    }

    public Decision<OrleansAppState> FilterByGrain(
        [Sample("AgentNeuron", "bad/grain?filter")] string? grainFilter)
    {
        if (Decision.Check("filter-grain", ValidGrainFilterRule(grainFilter)) is { } refused)
        {
            return refused;
        }

        var clean = grainFilter!.Trim();
        return Decision.Accept(this with
        {
            Section = "grains",
            GrainFilter = clean,
            Status = $"Orleans Grain Dashboard · Grains ({clean})",
        });
    }

    public OrleansAppState ClearGrainFilter() => this with
    {
        GrainFilter = null,
        Status = $"Orleans Grain Dashboard · {FormatSectionLabel(Section)}",
    };

    public OrleansAppState Reload() => this with
    {
        ReloadRevision = ReloadRevision == 0 ? 1 : 0,
        Status = $"Reloaded Orleans Grain Dashboard · {FormatSectionLabel(Section)}",
    };

    [IgnoreExplore]
    public OrleansAppState SetNavigationLock(bool locked) => this with
    {
        NavigationLocked = locked,
    };

    [IgnoreExplore]
    public Decision<OrleansAppState> ApplyNavigation(BrowserNavArgs? args, string transition = ViewTransition.FadeThrough)
    {
        var normalizedTransition = ViewTransition.Normalize(transition);
        if (args is null)
        {
            return Decision.Accept(this with { LastTransition = normalizedTransition });
        }

        var rules = new List<Rule>();
        if (!string.IsNullOrWhiteSpace(args.BaseUrl))
        {
            rules.Add(ValidDashboardBaseRule(args.BaseUrl));
        }

        var routeSection = !string.IsNullOrWhiteSpace(args.Section)
            ? args.Section
            : ExtractSectionFromRoute(args.Route, out _);
        var effectiveGrainFilter = !string.IsNullOrWhiteSpace(args.GrainFilter)
            ? args.GrainFilter
            : (!string.IsNullOrWhiteSpace(args.Section) ? null : ExtractGrainFromRoute(args.Route));

        if (!string.IsNullOrWhiteSpace(routeSection))
        {
            rules.Add(SupportedSectionRule(routeSection));
        }

        if (!string.IsNullOrWhiteSpace(effectiveGrainFilter))
        {
            rules.Add(ValidGrainFilterRule(effectiveGrainFilter));
        }

        if (rules.Count > 0 && Decision.Check("navigate-to", [.. rules]) is { } refused)
        {
            return refused;
        }

        var nextBase = DashboardBase;
        if (!string.IsNullOrWhiteSpace(args.BaseUrl) && TryNormalizeDashboardBase(args.BaseUrl, out var parsedBase))
        {
            nextBase = parsedBase!;
        }

        var nextSection = !string.IsNullOrWhiteSpace(effectiveGrainFilter)
            ? "grains"
            : (!string.IsNullOrWhiteSpace(routeSection) ? ResolveCanonicalSection(routeSection)! : Section);

        var nextFilter = !string.IsNullOrWhiteSpace(effectiveGrainFilter)
            ? effectiveGrainFilter!.Trim()
            : (string.Equals(nextSection, "grains", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(routeSection) ? GrainFilter : null);

        var detail = nextFilter is not null ? $"Grains ({nextFilter})" : FormatSectionLabel(nextSection);
        return Decision.Accept(this with
        {
            DashboardBase = nextBase,
            Section = nextSection,
            GrainFilter = nextFilter,
            LastTransition = normalizedTransition,
            Status = $"Orleans Grain Dashboard · {detail}",
        });
    }

    // ── Pure Validation & Formatting Helpers ─────────────────────────────────

    public static string? ResolveCanonicalSection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var slug = raw.Trim().TrimStart('#').Trim('/').ToLowerInvariant();
        return slug switch
        {
            "overview" or "dashboard" => "overview",
            "grains" or "grain" => "grains",
            "silos" or "silo" => "silos",
            "reminders" or "reminder" => "reminders",
            _ => null,
        };
    }

    public static string FormatSectionLabel(string section) => section switch
    {
        "overview" => "Overview",
        "grains" => "Grains",
        "silos" => "Silos",
        "reminders" => "Reminders",
        _ => "Overview",
    };

    public static bool IsValidDashboardBase(string? candidate)
        => TryNormalizeDashboardBase(candidate, out _);

    public static bool TryNormalizeDashboardBase(string? candidate, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, DefaultDashboardBase, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "/orleans/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = DefaultDashboardBase;
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        normalized = string.IsNullOrEmpty(path)
            ? $"{uri.GetLeftPart(UriPartial.Authority)}{DefaultDashboardBase}"
            : $"{uri.GetLeftPart(UriPartial.Authority)}{path}";
        return true;
    }

    public static bool IsValidGrainFilter(string? grainFilter)
    {
        if (string.IsNullOrWhiteSpace(grainFilter))
        {
            return false;
        }

        var trimmed = grainFilter.Trim();
        if (trimmed.Length is < 1 or > 128)
        {
            return false;
        }

        var hasLetterOrDigit = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                hasLetterOrDigit = true;
                continue;
            }

            if (ch is not ('_' or '-' or '.' or ':' or '+' or '`'))
            {
                return false;
            }
        }

        return hasLetterOrDigit;
    }

    private static string NormalizeDashboardBaseOrFallback(string? raw)
        => TryNormalizeDashboardBase(raw, out var normalized) ? normalized! : DefaultDashboardBase;

    private static string? ExtractSectionFromRoute(string? route, out string? grainFilter)
    {
        grainFilter = null;
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        var clean = route.Trim();
        var hashIdx = clean.IndexOf('#', StringComparison.Ordinal);
        if (hashIdx >= 0)
        {
            clean = clean[(hashIdx + 1)..];
        }
        else if (clean.StartsWith("/orleans", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["/orleans".Length..];
        }

        clean = clean.Trim('/');
        if (clean.Length == 0)
        {
            return null;
        }

        const string grainsPrefix = "grains/";
        if (clean.StartsWith(grainsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            grainFilter = clean[grainsPrefix.Length..].Trim('/');
            return "grains";
        }

        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? clean[..slash] : clean;
    }

    private static string? ExtractGrainFromRoute(string? route)
    {
        _ = ExtractSectionFromRoute(route, out var grainFilter);
        return grainFilter;
    }

    public override string ToString()
        => GrainFilter is { Length: > 0 } gf ? $"{Section}:{gf}" : Section;
}
