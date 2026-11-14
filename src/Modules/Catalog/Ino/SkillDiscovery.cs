using System.Text;
using DigitalBrain.Log;
using global::Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Metadata discovered from an installed or workspace micro-app's <c>SKILL.md</c> sidecar (<c>F-SKL-03</c>).
/// </summary>
public sealed record DiscoveredAppSkill(
    string Alias,
    string Name,
    string Title,
    string Description,
    IReadOnlyList<string> Capabilities,
    string? Schedule,
    string Source,
    string Summary);

/// <summary>
/// Discovers <c>SKILL.md</c> sidecars across deployed Synapse runtime units (<see cref="ISynapseStudio.Sidecars"/>)
/// and workspace/repository <c>apps/*/SKILL.md</c> folders so Ino automatically discovers micro-app capabilities.
/// </summary>
public static class SkillDiscovery
{
    public const string FileName = "SKILL.md";

    public static async Task<IReadOnlyList<DiscoveredAppSkill>> DiscoverAsync(
        ISynapseStudio? studio,
        string? workspaceAppsRoot = null,
        CancellationToken ct = default)
    {
        var byAlias = new Dictionary<string, DiscoveredAppSkill>(StringComparer.OrdinalIgnoreCase);

        if (studio is not null)
        {
            DigitalBrain.Units.DeployedSource[] deployed = [];
            try
            {
                deployed = await studio.List().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                NullLogger.Instance.LogSwallowed(ex, "SkillDiscovery could not list deployed units.");
            }

            foreach (var unit in deployed)
            {
                ct.ThrowIfCancellationRequested();
                var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
                if (string.IsNullOrWhiteSpace(alias)) { continue; }
                try
                {
                    var sidecars = await studio.Sidecars(alias).WaitAsync(ct).ConfigureAwait(false);
                    var skillText = FindSkillSidecar(sidecars);
                    if (!string.IsNullOrWhiteSpace(skillText))
                    {
                        byAlias[alias] = ParseSkillMarkdown(alias, skillText, "deployed-unit");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NullLogger.Instance.LogSwallowed(ex, $"SkillDiscovery could not read sidecars for '{alias}'.");
                }
            }
        }

        var root = workspaceAppsRoot ?? TryLocateDefaultAppsDirectory();
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var folderAlias = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderAlias) || byAlias.ContainsKey(folderAlias))
                {
                    continue;
                }

                var skillPath = Path.Combine(dir, FileName);
                if (!File.Exists(skillPath))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(skillPath, Encoding.UTF8, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var parsed = ParseSkillMarkdown(folderAlias, content, "apps-folder");
                    if (!byAlias.ContainsKey(parsed.Alias))
                    {
                        byAlias[parsed.Alias] = parsed;
                    }
                }
            }
        }

        return [.. byAlias.Values.OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)];
    }

    public static DiscoveredAppSkill ParseSkillMarkdown(string fallbackAlias, string markdown, string source = "sidecar")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackAlias);
        ArgumentNullException.ThrowIfNull(markdown);

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        string name = fallbackAlias.Trim();
        string? title = null;
        string? description = null;
        string? schedule = null;
        var capabilities = new List<string>();
        var body = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var endFence = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (endFence < 0 && normalized.EndsWith("\n---", StringComparison.Ordinal))
            {
                endFence = normalized.Length - 4;
            }

            if (endFence >= 4)
            {
                var frontmatter = normalized[4..endFence];
                body = endFence + 5 <= normalized.Length ? normalized[(endFence + 5)..] : string.Empty;
                var inCapabilitiesList = false;

                foreach (var rawLine in frontmatter.Split('\n'))
                {
                    var trimmed = rawLine.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    if (inCapabilitiesList && trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        var item = Unquote(trimmed[2..].Trim());
                        if (item.Length > 0)
                        {
                            capabilities.Add(item);
                        }
                        continue;
                    }

                    var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0)
                    {
                        inCapabilitiesList = false;
                        continue;
                    }

                    var key = trimmed[..colon].Trim().ToLowerInvariant();
                    var val = Unquote(trimmed[(colon + 1)..].Trim());
                    inCapabilitiesList = false;

                    switch (key)
                    {
                        case "name":
                            if (val.Length > 0) { name = val; }
                            break;
                        case "title":
                            if (val.Length > 0) { title = val; }
                            break;
                        case "description":
                            if (val.Length > 0) { description = val; }
                            break;
                        case "schedule":
                            if (val.Length > 0 && !string.Equals(val, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(val, "null", StringComparison.OrdinalIgnoreCase))
                            {
                                schedule = val;
                            }
                            break;
                        case "capabilities":
                            if (val.Length == 0)
                            {
                                inCapabilitiesList = true;
                            }
                            else if (val.StartsWith('[') && val.EndsWith(']'))
                            {
                                var inner = val[1..^1].Trim();
                                if (inner.Length > 0)
                                {
                                    foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                    {
                                        var clean = Unquote(part);
                                        if (clean.Length > 0) { capabilities.Add(clean); }
                                    }
                                }
                            }
                            else
                            {
                                capabilities.Add(val);
                            }
                            break;
                    }
                }
            }
        }

        var bodyLines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (title is null)
        {
            var heading = bodyLines.FirstOrDefault(l => l.StartsWith('#'));
            title = heading is not null ? heading.TrimStart('#').Trim() : name;
        }

        var firstParagraph = bodyLines.FirstOrDefault(l => !l.StartsWith('#') && !l.StartsWith('-')) ?? string.Empty;
        description ??= firstParagraph.Length > 0 ? firstParagraph : title;
        var summary = bodyLines.Length == 0
            ? description
            : string.Join(" ", bodyLines.Where(l => !l.StartsWith('#')).Take(3));
        if (summary.Length > 280)
        {
            summary = summary[..277] + "...";
        }

        return new DiscoveredAppSkill(
            Alias: fallbackAlias.Trim(),
            Name: name,
            Title: title,
            Description: description,
            Capabilities: capabilities,
            Schedule: schedule,
            Source: source,
            Summary: summary.Length > 0 ? summary : description);
    }

    public static string? FindSkillSidecar(IReadOnlyDictionary<string, string>? sidecars)
    {
        if (sidecars is null || sidecars.Count == 0) { return null; }
        if (sidecars.TryGetValue(FileName, out var exact)) { return exact; }
        return sidecars.FirstOrDefault(kv => string.Equals(Path.GetFileName(kv.Key), FileName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static string? TryLocateDefaultAppsDirectory(string? startDirectory = null)
    {
        try
        {
            var cursor = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
            while (cursor is not null)
            {
                var candidate = Path.Combine(cursor.FullName, "apps");
                if (Directory.Exists(candidate) && (File.Exists(Path.Combine(cursor.FullName, "Intochat.OS.slnx")) || Directory.Exists(Path.Combine(cursor.FullName, "src"))))
                {
                    return candidate;
                }
                cursor = cursor.Parent;
            }
        }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "SkillDiscovery could not probe default apps directory.");
        }

        return null;
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
        {
            return text[1..^1];
        }
        return text;
    }
}
