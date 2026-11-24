using System.Globalization;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.Microsoft.GitHub;

namespace IntoChat.OS.Modules.Apps;

internal sealed record IssueTriageGeneratedPackage(
    string Alias,
    string Slug,
    string ConnectionKey,
    string Mode,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<Scenario> Scenarios);

internal sealed record IssueTriageClassification(
    string Kind,
    string[] SuggestedLabels,
    string SanitizedTitle,
    string MarkdownSummary);

internal static class IssueTriageSynapseGenerator
{
    public const string DraftForApprovalMode = "DraftForApproval";
    public const string AutoCommentMode = "AutoComment";
    public const string CodeFileName = "IssueTriageSynapse.cs";

    private const int MaxSlugLength = 48;
    private const int MaxSanitizedTitleLength = 120;

    private static readonly JsonSerializerOptions ScenariosJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string[] SecurityKeywords =
    [
        "cve-",
        "vulnerability",
        "xss",
        "csrf",
        "sql injection",
        "command injection",
        "privilege escalation",
        "auth bypass",
        "authentication bypass",
        "remote code execution",
        "security advisory",
        "security bug",
        "security issue",
        "secret leak",
    ];

    private static readonly string[] BugKeywords =
    [
        "crash",
        "exception",
        "nullreference",
        "stack trace",
        "regression",
        "deadlock",
        "broken",
        "bug",
        "throws",
        "segfault",
        "timeout",
        "500",
        "404",
        "fails",
        "failed",
        "error",
    ];

    private static readonly string[] FeatureKeywords =
    [
        "feature",
        "enhancement",
        "proposal",
        "support ",
        "allow ",
        "add ",
        "implement ",
        "request:",
        "feature request",
    ];

    private static readonly string[] QuestionKeywords =
    [
        "question",
        "how to",
        "how do i",
        "clarify",
        "documentation",
        "docs:",
        "help:",
    ];

    public static bool TryNormalizeConnectionKey(
        string currentBrain,
        string? rawConnectionKey,
        out string normalizedConnectionKey,
        out string slug,
        out string error)
    {
        normalizedConnectionKey = string.Empty;
        slug = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawConnectionKey))
        {
            error = "A GitHub connection key or slug is required (for example, 'connection/github/main' or 'main').";
            return false;
        }

        var trimmed = rawConnectionKey.Trim();
        var segments = trimmed.Split('/');

        string rawSlug;
        switch (segments.Length)
        {
            case 1:
                rawSlug = segments[0];
                break;
            case 2 when string.Equals(segments[0], "github", StringComparison.OrdinalIgnoreCase):
                rawSlug = segments[1];
                break;
            case 3 when string.Equals(segments[0], "connection", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "github", StringComparison.OrdinalIgnoreCase):
                rawSlug = segments[2];
                break;
            case 4 when string.Equals(segments[1], "connection", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[2], "github", StringComparison.OrdinalIgnoreCase):
                if (!string.Equals(segments[0], currentBrain, StringComparison.Ordinal))
                {
                    error = $"Connection key '{trimmed}' belongs to brain '{segments[0]}', not '{currentBrain}'.";
                    return false;
                }
                rawSlug = segments[3];
                break;
            default:
                error = $"Invalid GitHub connection key '{trimmed}'. Expected 'connection/github/<slug>' or '<slug>'.";
                return false;
        }

        var candidate = rawSlug.Trim().ToLowerInvariant();
        if (!IsValidSlug(candidate))
        {
            error = $"Invalid GitHub connection slug '{rawSlug}'. Use 1–{MaxSlugLength} lowercase letters, digits, hyphens, or underscores starting with a letter or digit.";
            return false;
        }

        slug = candidate;
        normalizedConnectionKey = string.Concat("connection/github/", slug);
        return true;
    }

    public static bool TryNormalizeMode(string? rawMode, out string normalizedMode, out string error)
    {
        normalizedMode = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawMode))
        {
            error = $"Invalid triage mode. Specify '{DraftForApprovalMode}' or '{AutoCommentMode}'.";
            return false;
        }

        var trimmed = rawMode.Trim();
        if (string.Equals(trimmed, DraftForApprovalMode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "draft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "draft-for-approval", StringComparison.OrdinalIgnoreCase))
        {
            normalizedMode = DraftForApprovalMode;
            return true;
        }

        if (string.Equals(trimmed, AutoCommentMode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "auto-comment", StringComparison.OrdinalIgnoreCase))
        {
            normalizedMode = AutoCommentMode;
            return true;
        }

        error = $"Unsupported triage mode '{trimmed}'. Expected '{DraftForApprovalMode}' or '{AutoCommentMode}'.";
        return false;
    }

    public static IssueTriageClassification ClassifyIssue(int issueNumber, string action, IssueRead issueRead)
    {
        ArgumentNullException.ThrowIfNull(issueRead);

        var rawTitle = issueRead.Snapshot.Title ?? string.Empty;
        var sanitizedTitle = SanitizeUntrustedTitle(rawTitle, issueNumber);
        var unfencedBody = ExtractUnfencedEvidence(issueRead.Body);
        var existingLabels = issueRead.Snapshot.Labels ?? [];

        var kind = ClassifyText(rawTitle, unfencedBody, existingLabels);
        string[] labels = ["triage", kind];
        var fencedPresent = issueRead.Body.Contains(IssueRead.UntrustedEvidenceHeader, StringComparison.Ordinal);

        var summary = new StringBuilder()
            .Append("### Automated Issue Triage (`#")
            .Append(issueNumber.ToString(CultureInfo.InvariantCulture))
            .AppendLine("`)")
            .AppendLine()
            .Append("- **Classification**: `")
            .Append(kind)
            .AppendLine("`")
            .Append("- **Suggested labels**: `triage`, `")
            .Append(kind)
            .AppendLine("`")
            .Append("- **Trigger**: `")
            .Append(SanitizeInlineToken(action))
            .AppendLine("`")
            .Append("- **Untrusted title (fenced evidence)**: `")
            .Append(sanitizedTitle)
            .AppendLine("`")
            .Append("- **Safety**: Evaluated strictly from read-only repository evidence (")
            .Append(fencedPresent ? "untrusted-evidence fence verified" : "read-only snapshot")
            .Append("; zero code, branch, or secret write tools exposed).")
            .ToString();

        return new IssueTriageClassification(kind, labels, sanitizedTitle, summary);
    }

    public static string ClassifyText(string? title, string? body, IReadOnlyList<string>? existingLabels = null)
    {
        var lowerTitle = (title ?? string.Empty).ToLowerInvariant();
        var lowerBody = (body ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(lowerTitle, SecurityKeywords) || ContainsAny(lowerBody, SecurityKeywords))
        {
            return "security";
        }

        if (ContainsAny(lowerTitle, BugKeywords))
        {
            return "bug";
        }

        if (ContainsAny(lowerTitle, FeatureKeywords))
        {
            return "feature";
        }

        if (ContainsAny(lowerTitle, QuestionKeywords) || lowerTitle.TrimEnd().EndsWith('?'))
        {
            return "question";
        }

        if (ContainsAny(lowerBody, BugKeywords))
        {
            return "bug";
        }

        if (ContainsAny(lowerBody, FeatureKeywords))
        {
            return "feature";
        }

        if (ContainsAny(lowerBody, QuestionKeywords))
        {
            return "question";
        }

        if (existingLabels is { Count: > 0 })
        {
            foreach (var label in existingLabels)
            {
                if (label.Contains("security", StringComparison.OrdinalIgnoreCase)) { return "security"; }
                if (label.Contains("bug", StringComparison.OrdinalIgnoreCase)) { return "bug"; }
                if (label.Contains("feature", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("enhancement", StringComparison.OrdinalIgnoreCase))
                {
                    return "feature";
                }
            }
        }

        return "question";
    }

    public static IssueTriageGeneratedPackage Generate(string slug, string connectionKey, string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        var alias = string.Concat("gh-triage-", slug);
        var code = BuildSynapseCSharpSource(alias, slug, connectionKey, mode);
        var spec = BuildSpecMarkdown(alias, connectionKey, mode);
        var scenarios = BuildScenarios(alias, mode);
        var scenariosText = JsonSerializer.Serialize(scenarios, ScenariosJson);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CodeFileName] = code,
            [UnitFiles.Spec] = spec,
            [UnitFiles.Scenarios] = scenariosText,
        };

        return new IssueTriageGeneratedPackage(alias, slug, connectionKey, mode, files, scenarios);
    }

    private static bool IsValidSlug(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxSlugLength)
        {
            return false;
        }

        var first = candidate[0];
        if (!char.IsAsciiLetterLower(first) && !char.IsAsciiDigit(first))
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractUnfencedEvidence(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var markerIndex = body.IndexOf(IssueRead.UntrustedEvidenceHeader, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return body;
        }

        var afterHeader = markerIndex + IssueRead.UntrustedEvidenceHeader.Length;
        return afterHeader < body.Length ? body[afterHeader..].TrimStart() : string.Empty;
    }

    internal static string SanitizeUntrustedTitle(string? rawTitle, int issueNumber)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Issue #{issueNumber}");
        }

        var sb = new StringBuilder(Math.Min(rawTitle.Length, MaxSanitizedTitleLength));
        var previousSpace = false;
        foreach (var ch in rawTitle.Trim())
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                previousSpace = sb.Length > 0;
                continue;
            }

            if (ch is '`' or '<' or '>' or '[' or ']')
            {
                continue;
            }

            if (previousSpace)
            {
                sb.Append(' ');
                previousSpace = false;
            }

            sb.Append(ch);
            if (sb.Length >= MaxSanitizedTitleLength)
            {
                break;
            }
        }

        return sb.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Issue #{issueNumber}")
            : sb.ToString();
    }

    private static string SanitizeInlineToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "opened";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
        }

        return sb.Length == 0 ? "opened" : sb.ToString();
    }

    private static Scenario[] BuildScenarios(string alias, string mode) =>
    [
        new(
            "opened-closed-reopened-and-idempotence",
            JsonElement.Parse("{}"),
            [
                "publish github.issue-changed {\"number\":42,\"action\":\"opened\",\"version\":\"v1\",\"title\":\"Startup crash in Widget\"}",
                $"then state {alias} TriagedCount = 1",
                $"then state {alias} LastIssueNumber = 42",
                $"then state {alias} LastKind = \"bug\"",
                "then signal gh.triage.classified",
                $"then view {alias} contains \"#42\"",
                "publish github.issue-changed {\"number\":43,\"action\":\"closed\",\"version\":\"v1\",\"title\":\"Resolved issue\"}",
                $"then state {alias} TriagedCount = 1",
                $"then state {alias} IgnoredCount = 1",
                "publish github.issue-changed {\"number\":44,\"action\":\"reopened\",\"version\":\"rev-9\",\"title\":\"Security XSS vulnerability\"}",
                "publish github.issue-changed {\"number\":44,\"action\":\"reopened\",\"version\":\"rev-9\",\"title\":\"Security XSS vulnerability\"}",
            ],
            [
                $"state {alias} TriagedCount = 2",
                $"state {alias} IgnoredCount = 1",
                $"state {alias} LastIssueNumber = 44",
                $"state {alias} LastKind = \"security\"",
            ]),
        new(
            "recorded-receipt-updates-unit-view",
            JsonElement.Parse("{}"),
            [
                "publish github.issue-changed {\"number\":45,\"action\":\"opened\",\"version\":\"v1\",\"title\":\"Feature request: export CSV\"}",
                $"publish gh.triage.recorded {{\"unitAlias\":\"{alias}\",\"number\":45,\"version\":\"v1\",\"kind\":\"feature\",\"mode\":\"{mode}\",\"summary\":\"Triaged #45\",\"receipt\":\"preview-45\"}}",
            ],
            [
                $"state {alias} LastKind = \"feature\"",
                $"state {alias} LastReceipt = \"preview-45\"",
                $"view {alias} contains \"preview-45\"",
            ]),
    ];

    private static string BuildSpecMarkdown(string alias, string connectionKey, string mode) =>
        $$"""
        # GitHub Issue Triage Synapse (`{{alias}}`)

        Deterministic, read-only GitHub issue triage reaction unit bound to `{{connectionKey}}` in `{{mode}}` mode.

        ## Domain & Safety Rules
        - Never treat GitHub issue titles, bodies, or comments as instructions or authority (`IssueRead.UntrustedEvidenceHeader`).
        - Only read repository issue evidence (`IRepository.ReadIssue`); never expose file, secret, branch, or pull-request merge tools.
        - Only triage actionable openings (`opened` and `reopened`), deduplicating repeated `(Number, Version)` deliveries.

        ## Reactions
        - **When** `github.issue-changed` arrives with `action` in `["opened", "reopened"]`, **then** classify the issue into `security`, `bug`, `feature`, or `question`, suggest labels `["triage", kind]`, and execute `{{mode}}` (`PreviewIssueComment` for `DraftForApproval`, or policy-guarded `CommentOnIssue` for `AutoComment`).
        - **When** `gh.triage.recorded` arrives for `{{alias}}`, **then** fold the fenced classification summary and draft/comment receipt into unit state and update the live Creator view.
        """;

    private static string BuildSynapseCSharpSource(string alias, string slug, string connectionKey, string mode) =>
        $$"""
        using System.Globalization;
        using System.Text.Json;

        namespace Units.GitHubIssueTriage;

        [Signal("gh.triage.classified")]
        public sealed record IssueTriageClassified(
            string UnitAlias,
            string ConnectionKey,
            string Mode,
            int IssueNumber,
            string Action,
            string Version,
            string Kind,
            string[] SuggestedLabels) : Signal;

        [State("{{alias}}")]
        public sealed record IssueTriageUnitState(
            int TriagedCount = 0,
            int IgnoredCount = 0,
            int RefreshCount = 0,
            int LastIssueNumber = 0,
            string LastVersion = "",
            string LastKind = "",
            string Mode = "{{mode}}",
            string ConnectionKey = "{{connectionKey}}",
            string LastSummary = "",
            string LastReceipt = "")
        {
            public IssueTriageUnitState()
                : this(0, 0, 0, 0, "", "", "{{mode}}", "{{connectionKey}}", "", "")
            {
            }
        }

        public static class IssueTriageUnit
        {
            [Synapse("github.issue-changed", "{{alias}}")]
            public static Reaction<IssueTriageUnitState> OnIssueChanged(
                SignalEnvelope envelope,
                IssueTriageUnitState state)
            {
                var number = ReadInt(envelope.Payload, "number", "Number");
                var action = ReadString(envelope.Payload, "action", "Action");
                var version = ReadString(envelope.Payload, "version", "Version");
                var title = ReadString(envelope.Payload, "title", "Title");
                var body = ReadString(envelope.Payload, "body", "Body");

                if (number <= 0 || (action != "opened" && action != "reopened"))
                {
                    return Reaction<IssueTriageUnitState>.Of(
                        state with { IgnoredCount = state.IgnoredCount + 1 });
                }

                if (number == state.LastIssueNumber
                    && version.Length > 0
                    && string.Equals(version, state.LastVersion, StringComparison.Ordinal))
                {
                    return Reaction<IssueTriageUnitState>.Of(state);
                }

                var kind = Classify(title, body);
                string[] labels = ["triage", kind];
                var summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Issue #{number} classified as {kind} ({state.Mode}).");

                var next = state with
                {
                    TriagedCount = state.TriagedCount + 1,
                    LastIssueNumber = number,
                    LastVersion = version,
                    LastKind = kind,
                    LastSummary = summary,
                };

                return Reaction<IssueTriageUnitState>.Of(
                    next,
                    new IssueTriageClassified(
                        "{{alias}}",
                        state.ConnectionKey,
                        state.Mode,
                        number,
                        action,
                        version,
                        kind,
                        labels));
            }

            [Synapse("gh.triage.recorded", "{{alias}}")]
            public static IssueTriageUnitState OnTriageRecorded(
                SignalEnvelope envelope,
                IssueTriageUnitState state)
            {
                var targetAlias = ReadString(envelope.Payload, "unitAlias", "UnitAlias");
                if (targetAlias.Length > 0 && !string.Equals(targetAlias, "{{alias}}", StringComparison.Ordinal))
                {
                    return state;
                }

                var number = ReadInt(envelope.Payload, "number", "Number");
                var version = ReadString(envelope.Payload, "version", "Version");
                var kind = ReadString(envelope.Payload, "kind", "Kind");
                var recordedMode = ReadString(envelope.Payload, "mode", "Mode");
                var summary = ReadString(envelope.Payload, "summary", "Summary");
                var receipt = ReadString(envelope.Payload, "receipt", "Receipt");

                return state with
                {
                    TriagedCount = number > 0 && state.LastIssueNumber != number ? state.TriagedCount + 1 : state.TriagedCount,
                    LastIssueNumber = number > 0 ? number : state.LastIssueNumber,
                    LastVersion = version.Length > 0 ? version : state.LastVersion,
                    LastKind = kind.Length > 0 ? kind : state.LastKind,
                    Mode = recordedMode.Length > 0 ? recordedMode : state.Mode,
                    LastSummary = summary.Length > 0 ? summary : state.LastSummary,
                    LastReceipt = receipt.Length > 0 ? receipt : state.LastReceipt,
                };
            }

            [Synapse("gh.triage.blocked", "{{alias}}")]
            public static IssueTriageUnitState OnTriageBlocked(
                SignalEnvelope envelope,
                IssueTriageUnitState state)
            {
                var targetAlias = ReadString(envelope.Payload, "unitAlias", "UnitAlias");
                if (targetAlias.Length > 0 && !string.Equals(targetAlias, "{{alias}}", StringComparison.Ordinal))
                {
                    return state;
                }

                return state with { LastReceipt = "blocked:github.issue.comment" };
            }

            [Synapse("gh.triage.refresh", "{{alias}}")]
            public static IssueTriageUnitState OnRefresh(
                SignalEnvelope envelope,
                IssueTriageUnitState state)
            {
                _ = envelope;
                return state with { RefreshCount = state.RefreshCount + 1 };
            }

            [Synapse("{{alias}}")]
            public static View Show(IssueTriageUnitState state)
            {
                var headline = state.LastIssueNumber > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Last triaged: #{state.LastIssueNumber} ({state.LastKind})")
                    : "Awaiting opened or reopened GitHub issues.";
                var receiptLine = state.LastReceipt.Length > 0
                    ? $"Latest receipt: {state.LastReceipt}"
                    : $"Connection: {state.ConnectionKey}";

                return new View(
                    "GitHub Issue Triage ({{slug}})",
                    new Surface(
                        [
                            new Row(
                            [
                                new Badge(state.Mode, state.Mode == "AutoComment" ? "warning" : "accent"),
                                new Badge(state.ConnectionKey, "neutral"),
                            ]),
                            new StatCardNode(
                                "triaged-count",
                                "Triaged Issues",
                                state.TriagedCount.ToString(CultureInfo.InvariantCulture)),
                            new Text(headline),
                            new Text(receiptLine),
                            new Button(
                                "refresh-triage",
                                "Refresh Triage",
                                new Command("{{alias}}", "gh.triage.refresh")),
                        ],
                        Title: "GitHub Issue Triage · {{slug}}",
                        Subtitle: state.ConnectionKey));
            }

            private static string Classify(string title, string body)
            {
                var combined = (title + "\n" + body).ToLowerInvariant();
                if (combined.Contains("cve-", StringComparison.Ordinal)
                    || combined.Contains("vulnerability", StringComparison.Ordinal)
                    || combined.Contains("xss", StringComparison.Ordinal)
                    || combined.Contains("csrf", StringComparison.Ordinal)
                    || combined.Contains("injection", StringComparison.Ordinal)
                    || combined.Contains("security", StringComparison.Ordinal))
                {
                    return "security";
                }

                if (combined.Contains("crash", StringComparison.Ordinal)
                    || combined.Contains("exception", StringComparison.Ordinal)
                    || combined.Contains("stack trace", StringComparison.Ordinal)
                    || combined.Contains("regression", StringComparison.Ordinal)
                    || combined.Contains("bug", StringComparison.Ordinal)
                    || combined.Contains("broken", StringComparison.Ordinal))
                {
                    return "bug";
                }

                if (combined.Contains("feature", StringComparison.Ordinal)
                    || combined.Contains("enhancement", StringComparison.Ordinal)
                    || combined.Contains("proposal", StringComparison.Ordinal)
                    || combined.Contains("support ", StringComparison.Ordinal)
                    || combined.Contains("add ", StringComparison.Ordinal))
                {
                    return "feature";
                }

                return "question";
            }

            private static int ReadInt(JsonElement payload, string camel, string pascal)
            {
                if (payload.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }

                if (payload.TryGetProperty(camel, out var c) && c.TryGetInt32(out var cv))
                {
                    return cv;
                }

                if (payload.TryGetProperty(pascal, out var p) && p.TryGetInt32(out var pv))
                {
                    return pv;
                }

                return 0;
            }

            private static string ReadString(JsonElement payload, string camel, string pascal)
            {
                if (payload.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                if (payload.TryGetProperty(camel, out var c) && c.ValueKind == JsonValueKind.String)
                {
                    return c.GetString() ?? string.Empty;
                }

                if (payload.TryGetProperty(pascal, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    return p.GetString() ?? string.Empty;
                }

                return string.Empty;
            }
        }
        """;
}
