using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Markdraw;

public static partial class MarkdrawSceneEngine
{
    public static Card BuildMarkdrawCanvasCard(
        string key,
        string title,
        string activeDocument,
        string format,
        string content,
        int revision,
        string status,
        MarkdrawElementSummary[] elements,
        string[] workspaceDrawings,
        string? lastExportPath = null,
        int height = 560)
    {
        var metaJson = JsonSerializer.Serialize(new
        {
            _ui = "markdraw",
            key,
            title,
            activeDocument,
            format,
            content,
            revision,
            status,
            lastExportPath,
            height,
            workspaceDrawings,
            elements = elements.Select(e => new
            {
                id = e.Id,
                kind = e.Kind,
                label = e.Label,
                x = e.X,
                y = e.Y,
                width = e.Width,
                height = e.Height,
                fromId = e.FromId,
                toId = e.ToId,
                color = e.Color,
            }).ToArray(),
        });

        return new Card(
            title,
            metaJson,
            [
                new Text($"{status} · {activeDocument} (rev {revision})") { Tone = "muted", Tabular = true },
            ]);
    }

    public static (string MergedContent, string Summary) ApplyCoDrawingPatch(
        string currentContent,
        string? prompt,
        string? explicitDsl,
        string? removeIdsCsv)
    {
        var baseContent = string.IsNullOrWhiteSpace(currentContent) ? MarkdrawApp.DefaultArchitectureScene : currentContent;
        var (header, bodyLines, footer) = SplitMarkdrawBlock(baseContent);

        var removedIds = new HashSet<string>(
            (removeIdsCsv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            foreach (var removeId in ExtractRemovalsFromPrompt(prompt))
            {
                removedIds.Add(removeId);
            }
        }

        var existingElements = ParseSceneElements(baseContent);
        var dslToMerge = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDsl))
        {
            foreach (var rawLine in explicitDsl.Replace("\\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    dslToMerge.Add(trimmed);
                }
            }
        }

        if (dslToMerge.Count == 0 && !string.IsNullOrWhiteSpace(prompt))
        {
            dslToMerge.AddRange(SynthesizeDslFromPrompt(prompt, existingElements));
        }

        var keptLines = new List<string>();
        var lineIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in bodyLines)
        {
            var parsed = TryParseDslLine(line, keptLines.Count);
            if (parsed is not null)
            {
                if (removedIds.Contains(parsed.Id)
                    || (!string.IsNullOrWhiteSpace(parsed.FromId) && removedIds.Contains(parsed.FromId))
                    || (!string.IsNullOrWhiteSpace(parsed.ToId) && removedIds.Contains(parsed.ToId)))
                {
                    continue;
                }

                lineIndexById[parsed.Id] = keptLines.Count;
            }

            keptLines.Add(line);
        }

        var addedCount = 0;
        var updatedCount = 0;
        foreach (var newLine in dslToMerge)
        {
            var parsed = TryParseDslLine(newLine, keptLines.Count + addedCount);
            if (parsed is not null && lineIndexById.TryGetValue(parsed.Id, out var existingIdx))
            {
                keptLines[existingIdx] = newLine;
                updatedCount++;
            }
            else
            {
                if (parsed is not null)
                {
                    lineIndexById[parsed.Id] = keptLines.Count;
                }

                keptLines.Add(newLine);
                addedCount++;
            }
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(header))
        {
            builder.AppendLine(header.TrimEnd());
            builder.AppendLine();
        }

        builder.AppendLine("```markdraw");
        foreach (var line in keptLines)
        {
            builder.AppendLine(line);
        }
        builder.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(footer))
        {
            builder.AppendLine();
            builder.AppendLine(footer.Trim());
        }

        var summaryParts = new List<string>();
        if (addedCount > 0)
        {
            summaryParts.Add($"added {addedCount} element(s)");
        }
        if (updatedCount > 0)
        {
            summaryParts.Add($"updated {updatedCount} element(s)");
        }
        if (removedIds.Count > 0)
        {
            summaryParts.Add($"removed {removedIds.Count} element(s)");
        }
        if (summaryParts.Count == 0)
        {
            summaryParts.Add("inspected canvas");
        }

        return (builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(), $"Co-drawing patch ({string.Join(", ", summaryParts)})");
    }

    public static string BuildDataLineageMarkdraw(
        string title,
        IReadOnlyList<string> columns,
        string rowsSummary,
        string sourceUri)
    {
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "Dataset Analysis Flow" : title.Trim();
        var colList = columns.Count > 0 ? string.Join(", ", columns.Take(5)) : "Opportunity, Account, Stage, Amount";

        return $$"""
            # {{safeTitle}} — Data Lineage & Visual Schema

            Generated by **Ino + Markdraw Studio** from `{{sourceUri}}` ({{rowsSummary}}).

            ```markdraw
            @background #0f172a
            @grid 20

            frame "{{safeTitle}} · Data Flow" id=data-frame at 40,40 size 880x400
            rect "Source: {{sourceUri}}" id=source-node at 80,110 size 250x80 fill=#1e3a8a color=#60a5fa rounded=10
            rect "Schema ({{colList}})" id=schema-node at 390,110 size 240x80 fill=#064e3b color=#34d399 rounded=10
            diamond "Aggregation & Filter" id=agg-node at 690,105 size 200x90 fill=#422006 color=#f59e0b
            rect "{{rowsSummary}}" id=summary-node at 220,275 size 280x80 fill=#1e1b4b color=#818cf8 rounded=10
            rect "Interactive Chart & CSV Export" id=chart-output at 560,275 size 260x80 fill=#3b0764 color=#c084fc rounded=10

            arrow source-node -> schema-node "Load Columns" color=#60a5fa
            arrow schema-node -> agg-node "Query & Group" color=#34d399
            arrow agg-node -> summary-node "Metrics" color=#818cf8
            arrow agg-node -> chart-output "Render UI" color=#c084fc
            ```
            """;
    }

    public static MarkdrawElementSummary[] ParseSceneElements(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith('{') && trimmed.Contains("\"elements\"", StringComparison.Ordinal))
        {
            return ParseExcalidrawJsonElements(trimmed);
        }

        var (_, bodyLines, _) = SplitMarkdrawBlock(trimmed);
        var elements = new List<MarkdrawElementSummary>();
        var idx = 0;
        foreach (var rawLine in bodyLines)
        {
            idx++;
            if (TryParseDslLine(rawLine, idx) is { } element)
            {
                elements.Add(element);
            }
        }

        return elements.ToArray();
    }

    public static string RenderExcalidrawJson(IReadOnlyList<MarkdrawElementSummary> elements)
    {
        var excalidrawElements = elements.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["type"] = e.Kind switch
            {
                "rect" => "rectangle",
                "FromId" => "arrow",
                _ => e.Kind,
            },
            ["x"] = e.X,
            ["y"] = e.Y,
            ["width"] = e.Width,
            ["height"] = e.Height,
            ["strokeColor"] = e.Color ?? "#10b981",
            ["backgroundColor"] = "#1e293b",
            ["text"] = e.Label,
            ["fromId"] = e.FromId,
            ["toId"] = e.ToId,
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            type = "excalidraw",
            version = 2,
            source = "neuron://local/apps/markdraw",
            elements = excalidrawElements,
            appState = new
            {
                viewBackgroundColor = "#0f172a",
                gridSize = 20,
            },
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string RenderSvgPreview(IReadOnlyList<MarkdrawElementSummary> elements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 540\" width=\"960\" height=\"540\">");
        sb.AppendLine("  <rect width=\"960\" height=\"540\" fill=\"#0f172a\" rx=\"12\" />");
        var byId = elements.Where(e => e.Kind is not ("arrow" or "line")).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var el in elements)
        {
            var stroke = string.IsNullOrWhiteSpace(el.Color) ? "#10b981" : el.Color;
            if (el.Kind is "arrow" or "line")
            {
                if (!string.IsNullOrWhiteSpace(el.FromId)
                    && !string.IsNullOrWhiteSpace(el.ToId)
                    && byId.TryGetValue(el.FromId, out var fromEl)
                    && byId.TryGetValue(el.ToId, out var toEl))
                {
                    var x1 = fromEl.X + fromEl.Width / 2;
                    var y1 = fromEl.Y + fromEl.Height / 2;
                    var x2 = toEl.X + toEl.Width / 2;
                    var y2 = toEl.Y + toEl.Height / 2;
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"{stroke}\" stroke-width=\"2.5\" />");
                }
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  <rect x=\"{el.X}\" y=\"{el.Y}\" width=\"{el.Width}\" height=\"{el.Height}\" rx=\"8\" fill=\"#1e293b\" stroke=\"{stroke}\" stroke-width=\"2\" />");
            var textX = el.X + 12;
            var textY = el.Y + el.Height / 2 + 5;
            var safeLabel = System.Security.SecurityElement.Escape(el.Label) ?? el.Id;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <text x=\"{textX}\" y=\"{textY}\" fill=\"#f8fafc\" font-family=\"Inter, sans-serif\" font-size=\"13\">{safeLabel}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static string SlugId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "node";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "node";
    }

    private static (string Header, List<string> BodyLines, string Footer) SplitMarkdrawBlock(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var fenceStart = normalized.IndexOf("```markdraw", StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0)
        {
            var header = normalized[..fenceStart].TrimEnd();
            var afterStart = normalized.IndexOf('\n', fenceStart);
            if (afterStart < 0)
            {
                return (header, [], string.Empty);
            }

            var fenceEnd = normalized.IndexOf("```", afterStart + 1, StringComparison.Ordinal);
            var blockBody = fenceEnd >= 0
                ? normalized[(afterStart + 1)..fenceEnd]
                : normalized[(afterStart + 1)..];
            var footer = fenceEnd >= 0 && fenceEnd + 3 < normalized.Length
                ? normalized[(fenceEnd + 3)..].Trim()
                : string.Empty;

            var lines = blockBody
                .Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            return (header, lines, footer);
        }

        var rawLines = normalized
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToList();
        return ("# Markdraw Canvas", rawLines, string.Empty);
    }

    private static MarkdrawElementSummary? TryParseDslLine(string rawLine, int fallbackIndex)
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith('@') || line.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var arrowMatch = ArrowLineRegex().Match(line);
        if (arrowMatch.Success)
        {
            var fromId = arrowMatch.Groups["from"].Value.Trim();
            var toId = arrowMatch.Groups["to"].Value.Trim();
            var label = arrowMatch.Groups["label"].Success ? arrowMatch.Groups["label"].Value : string.Empty;
            var color = MatchTokenValue(line, "color") ?? "#10b981";
            var explicitId = MatchTokenValue(line, "id") ?? $"arrow-{SlugId(fromId)}-{SlugId(toId)}";
            return new MarkdrawElementSummary(
                Id: explicitId,
                Kind: "arrow",
                Label: label,
                X: 0,
                Y: 0,
                Width: 0,
                Height: 0,
                FromId: fromId,
                ToId: toId,
                Color: color);
        }

        var shapeMatch = ShapeLineRegex().Match(line);
        if (!shapeMatch.Success)
        {
            if (line.StartsWith("freedraw", StringComparison.OrdinalIgnoreCase))
            {
                var strokeId = MatchTokenValue(line, "id") ?? $"freedraw-{fallbackIndex}";
                return new MarkdrawElementSummary(strokeId, "freedraw", "User Freehand Stroke", 0, 0, 120, 60, Color: "#f59e0b");
            }

            return null;
        }

        var kind = shapeMatch.Groups["kind"].Value.ToLowerInvariant();
        var shapeLabel = shapeMatch.Groups["label"].Value;
        var id = MatchTokenValue(line, "id") ?? $"{kind}-{SlugId(shapeLabel)}";
        var (x, y) = ParsePair(MatchTokenValue(line, "at"), ',', 80 + fallbackIndex % 3 * 260, 110 + fallbackIndex / 3 * 140);
        var (w, h) = ParsePair(MatchTokenValue(line, "size"), 'x', kind == "frame" ? 840 : 210, kind == "frame" ? 400 : 80);
        var strokeColor = MatchTokenValue(line, "color") ?? MatchTokenValue(line, "fill") ?? "#10b981";

        return new MarkdrawElementSummary(
            Id: id,
            Kind: kind,
            Label: shapeLabel,
            X: x,
            Y: y,
            Width: w,
            Height: h,
            Color: strokeColor);
    }

    private static MarkdrawElementSummary[] ParseExcalidrawJsonElements(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<MarkdrawElementSummary>();
            var i = 0;
            foreach (var item in arr.EnumerateArray())
            {
                i++;
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? $"el-{i}" : $"el-{i}";
                var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "rect" : "rect";
                var label = item.TryGetProperty("text", out var txtProp) ? txtProp.GetString() ?? id : id;
                var x = item.TryGetProperty("x", out var xProp) && xProp.TryGetDouble(out var xd) ? (int)xd : 100;
                var y = item.TryGetProperty("y", out var yProp) && yProp.TryGetDouble(out var yd) ? (int)yd : 100;
                var w = item.TryGetProperty("width", out var wProp) && wProp.TryGetDouble(out var wd) ? (int)wd : 180;
                var h = item.TryGetProperty("height", out var hProp) && hProp.TryGetDouble(out var hd) ? (int)hd : 80;
                var fromId = item.TryGetProperty("fromId", out var fProp) ? fProp.GetString() : null;
                var toId = item.TryGetProperty("toId", out var toProp) ? toProp.GetString() : null;
                var color = item.TryGetProperty("strokeColor", out var cProp) ? cProp.GetString() : "#10b981";
                results.Add(new MarkdrawElementSummary(
                    id,
                    type == "rectangle" ? "rect" : type,
                    label,
                    x,
                    y,
                    w,
                    h,
                    fromId,
                    toId,
                    color));
            }

            return results.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> SynthesizeDslFromPrompt(string prompt, IReadOnlyList<MarkdrawElementSummary> existing)
    {
        var results = new List<string>();
        var trimmed = prompt.Trim();

        foreach (var line in trimmed.Split('\n'))
        {
            if (TryParseDslLine(line, existing.Count + 1) is not null)
            {
                results.Add(line.Trim());
            }
        }
        if (results.Count > 0)
        {
            return results;
        }

        var quoted = QuotedLabelRegex().Matches(trimmed)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (quoted.Count == 0)
        {
            var cleaned = Regex.Replace(
                trimmed,
                @"^(?:please\s+)?(?:in\s+markdraw\s+|on\s+the\s+canvas\s+)?(?:add|draw|sketch|create|diagram|connect|patch|put)\s+(?:a\s+|an\s+|the\s+)?",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();

            var parts = Regex.Split(cleaned, @"\s+(?:and|connected\s+to|->|to|with)\s+", RegexOptions.IgnoreCase)
                .Select(p => Regex.Replace(p, @"\b(?:node|box|shape|rect|rectangle|ellipse|diamond|on\s+canvas|in\s+markdraw)\b", string.Empty, RegexOptions.IgnoreCase).Trim(' ', '.', ',', '"', '\''))
                .Where(p => p.Length >= 2)
                .Take(4)
                .ToList();

            quoted.AddRange(parts);
        }

        if (quoted.Count == 0)
        {
            quoted.Add("Ino Co-Drawn Node");
        }

        var shapes = existing.Where(e => e.Kind is not ("arrow" or "line" or "frame")).ToList();
        var baseIndex = shapes.Count;
        var previousId = shapes.LastOrDefault()?.Id;

        var shapeKind = trimmed.Contains("diamond", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("decision", StringComparison.OrdinalIgnoreCase)
            ? "diamond"
            : trimmed.Contains("ellipse", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("circle", StringComparison.OrdinalIgnoreCase)
                ? "ellipse"
                : "rect";

        foreach (var label in quoted)
        {
            var id = SlugId(label);
            var existingMatch = existing.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)
                || e.Label.Contains(label, StringComparison.OrdinalIgnoreCase));

            if (existingMatch is not null)
            {
                previousId = existingMatch.Id;
                continue;
            }

            var col = baseIndex % 3;
            var row = baseIndex / 3;
            var x = 80 + col * 280;
            var y = 110 + row * 145;
            baseIndex++;

            results.Add($"{shapeKind} \"{label}\" id={id} at {x},{y} size 220x80 fill=#064e3b color=#10b981 rounded=10");
            if (!string.IsNullOrWhiteSpace(previousId) && !string.Equals(previousId, id, StringComparison.OrdinalIgnoreCase))
            {
                results.Add($"arrow {previousId} -> {id} \"Co-drawn\" color=#34d399");
            }
            previousId = id;
        }

        return results;
    }

    private static IEnumerable<string> ExtractRemovalsFromPrompt(string prompt)
    {
        var match = RemoveNodeRegex().Match(prompt);
        if (!match.Success)
        {
            yield break;
        }

        var target = match.Groups["id"].Value.Trim(' ', '"', '\'', '.', ',');
        if (!string.IsNullOrWhiteSpace(target))
        {
            yield return SlugId(target);
            yield return target;
        }
    }

    private static string? MatchTokenValue(string line, string key)
    {
        var pattern = $@"\b{Regex.Escape(key)}=(?<val>[^\s]+)";
        var m = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["val"].Value.Trim('"', '\'') : null;
    }

    private static (int First, int Second) ParsePair(string? raw, char separator, int defaultFirst, int defaultSecond)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (defaultFirst, defaultSecond);
        }

        var parts = raw.Split(separator, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            return (a, b);
        }

        return (defaultFirst, defaultSecond);
    }

    [GeneratedRegex(@"^(?<kind>rect|ellipse|diamond|frame|text|note)\s+""(?<label>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ShapeLineRegex();

    [GeneratedRegex(@"^arrow\s+(?<from>[^\s]+)\s*->\s*(?<to>[^\s]+)(?:\s+""(?<label>[^""]*)"")?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArrowLineRegex();

    [GeneratedRegex(@"""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex QuotedLabelRegex();

    [GeneratedRegex(@"\b(?:remove|delete|drop)\s+(?:node|box|shape|element)?\s*(?<id>[a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RemoveNodeRegex();
}
