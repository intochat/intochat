namespace DigitalBrain.UI;

/// <summary>Ergonomic high-level builders for authoring <c>App&lt;TState&gt;</c> desktop views in ~30 lines of C#.</summary>
public static class Ui
{
    /// <summary>Creates a studio-grade desktop app shell layout with optional sidebar, toolbar, and right inspector pane.</summary>
    public static AppShellNode AppShell(
        string key,
        Node? sidebar,
        Node body,
        Node? toolbar = null,
        Node? inspector = null,
        string automationName = "",
        string transition = "",
        bool edgeToEdge = false) =>
        new(key, sidebar, body, toolbar, inspector, automationName, transition, edgeToEdge);

    /// <summary>Creates a sidebar navigation item with a Lucide icon key, operation name, selection state, and optional badge.</summary>
    public static NavItemNode NavItem(
        string key,
        string label,
        string icon,
        string action,
        bool selected = false,
        string? badge = null,
        Command? command = null,
        string automationName = "") =>
        new(key, label, icon, action, selected, badge, command, string.IsNullOrEmpty(automationName) ? label : automationName);

    /// <summary>Creates a tabular-numeral (<c>tnum</c>) KPI stat card with optional delta pill and semantic tone.</summary>
    public static StatCardNode StatCard(
        string key,
        string label,
        string value,
        string? delta = null,
        BadgeTone tone = BadgeTone.Neutral,
        string? icon = null,
        string automationName = "") =>
        new(key, label, value, delta, tone, icon, automationName);

    /// <summary>Creates a declarative WASM / Web studio browser surface for <paramref name="url"/>.</summary>
    public static WebBrowser WebBrowser(
        string url,
        string? title = null,
        string key = "web-browser",
        double height = 520,
        string executionMode = "wasm-ui",
        string automationName = "",
        string chrome = "full") =>
        new(key, url, title, height, executionMode, automationName, chrome);

    /// <summary>Creates a micro-app telemetry tile with optional sparkline, threshold, and quick actions.</summary>
    public static MetricTileNode MetricTile(
        string id,
        string appId,
        string title,
        string value,
        string? delta = null,
        string deltaTone = "neutral",
        string? statusLabel = null,
        string statusTone = "neutral",
        IReadOnlyList<double>? sparkline = null,
        double? threshold = null,
        IReadOnlyList<Button>? actions = null) =>
        new(id, appId, title, value, delta, deltaTone, statusLabel, statusTone, sparkline, threshold, actions);

    /// <summary>Creates a structured diff preview card for Feature-to-PR workflows.</summary>
    public static DiffCardNode DiffCard(
        string id,
        string repo,
        string targetBranch,
        string headSha,
        int filesChanged,
        int additions,
        int deletions,
        IReadOnlyList<DiffFileNode> files) =>
        new(id, repo, targetBranch, headSha, filesChanged, additions, deletions, files);
}
