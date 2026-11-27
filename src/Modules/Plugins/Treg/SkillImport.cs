using System.Globalization;
using System.Text;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.Treg;

/// <summary>
/// Pure importer that projects a <c>SKILL.md</c> document into a validated pure-data agent <see cref="AppManifest"/>
/// declaring one required <c>treg</c> (<c>tool@1</c>) capability slot.
/// </summary>
public static class SkillImport
{
    private const int MaxDerivedDescriptionLength = 240;

    /// <summary>
    /// Parses a <see cref="SkillFile"/> into a validated Rung 1 agent <see cref="AppManifest"/>.
    /// </summary>
    public static AppManifest ToApp(
        SkillFile file,
        IReadOnlyList<string>? starters = null,
        ModelSelection? model = null,
        string? handle = null,
        string[]? allowedContracts = null,
        string[]? toolSources = null,
        MemoryScope? memoryScope = null,
        bool includeTregSlot = true)
    {
        ArgumentNullException.ThrowIfNull(file);

        var parsed = Parse(file);
        if (string.IsNullOrWhiteSpace(parsed.Instructions))
        {
            throw new InvalidDataException("Skill file must contain non-empty Markdown instructions.");
        }

        var title = ResolveTitle(parsed, file.Name);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidDataException("Skill file must declare a title, name, or top-level heading.");
        }

        var description = ResolveDescription(parsed, title);
        var resolvedStarters = ResolveStarters(starters, parsed.FrontmatterStarters, parsed.BodyStarters);
        var resolvedHandle = !string.IsNullOrWhiteSpace(handle) ? handle : parsed.Handle;
        var resolvedContracts = allowedContracts is not null
            ? allowedContracts
            : parsed.AllowedContracts.Length > 0
                ? parsed.AllowedContracts
                : null;
        var resolvedToolSources = toolSources is not null
            ? toolSources
            : parsed.ToolSources.Length > 0
                ? parsed.ToolSources
                : null;
        var resolvedMemoryScope = memoryScope ?? parsed.MemoryScope;

        var manifest = new AppManifest(
            Title: title,
            Description: description,
            Operations: [],
            Settings: [],
            Slots: includeTregSlot ? [TregPreset.Slot(required: true)] : [],
            Icon: parsed.Icon,
            Category: parsed.Category ?? "Skills",
            Launch: parsed.Launch,
            Agent: new AppAgent(
                Instructions: parsed.Instructions,
                Model: model,
                Starters: resolvedStarters,
                Tools: includeTregSlot ? [TregPreset.SlotName] : [],
                Handle: resolvedHandle,
                AllowedContracts: resolvedContracts,
                ToolSources: resolvedToolSources,
                MemoryScope: resolvedMemoryScope));

        return manifest.Validated();
    }

    /// <summary>
    /// Computes a deterministic, lowercase ASCII app id slug from the skill's frontmatter <c>name</c>
    /// or fallback <see cref="SkillFile.Name"/>.
    /// </summary>
    public static string AppIdOf(SkillFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var parsed = Parse(file);
        var raw = !string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Name : StripSkillSuffix(file.Name);
        var slug = ToSlug(raw);
        if (slug.Length == 0)
        {
            throw new InvalidDataException($"Skill file '{file.Name}' cannot produce a valid app id slug.");
        }

        return slug;
    }

    private sealed record ParsedSkill(
        string? Name,
        string? Title,
        string? Description,
        string? Icon,
        string? Category,
        AppLaunch Launch,
        string? Handle,
        string[] AllowedContracts,
        string[] ToolSources,
        MemoryScope MemoryScope,
        string[] FrontmatterStarters,
        string[] BodyStarters,
        string? FirstHeading,
        string? FirstParagraph,
        string Instructions);

    private static ParsedSkill Parse(SkillFile file)
    {
        var normalized = NormalizeNewlines(file.Content ?? string.Empty);
        string? name = null;
        string? title = null;
        string? description = null;
        string? icon = null;
        string? category = null;
        string? handle = null;
        var launch = AppLaunch.Windowed;
        var memoryScope = MemoryScope.None;
        var frontmatterStarters = new List<string>();
        var allowedContracts = new List<string>();
        var toolSources = new List<string>();
        var body = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            if (!TrySplitFrontmatter(normalized, out var frontmatter, out var remainder))
            {
                throw new InvalidDataException("Skill file frontmatter is missing a closing '---' fence.");
            }

            body = remainder;
            ParseFrontmatter(
                frontmatter,
                ref name,
                ref title,
                ref description,
                ref icon,
                ref category,
                ref launch,
                ref handle,
                ref memoryScope,
                frontmatterStarters,
                allowedContracts,
                toolSources);
        }

        var instructions = body.Trim();
        var (firstHeading, firstParagraph, bodyStarters) = InspectBody(instructions);

        return new ParsedSkill(
            Name: name,
            Title: title,
            Description: description,
            Icon: icon,
            Category: category,
            Launch: launch,
            Handle: handle,
            AllowedContracts: [.. allowedContracts],
            ToolSources: [.. toolSources],
            MemoryScope: memoryScope,
            FrontmatterStarters: [.. frontmatterStarters],
            BodyStarters: bodyStarters,
            FirstHeading: firstHeading,
            FirstParagraph: firstParagraph,
            Instructions: instructions);
    }

    private static string NormalizeNewlines(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static bool TrySplitFrontmatter(string normalized, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = normalized;

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        var endFence = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endFence >= 4)
        {
            frontmatter = normalized[4..endFence];
            body = normalized[(endFence + 5)..];
            return true;
        }

        if (normalized.EndsWith("\n---", StringComparison.Ordinal))
        {
            endFence = normalized.Length - 4;
            if (endFence >= 4)
            {
                frontmatter = normalized[4..endFence];
                body = string.Empty;
                return true;
            }
        }

        return false;
    }

    private static void ParseFrontmatter(
        string frontmatter,
        ref string? name,
        ref string? title,
        ref string? description,
        ref string? icon,
        ref string? category,
        ref AppLaunch launch,
        ref string? handle,
        ref MemoryScope memoryScope,
        List<string> starters,
        List<string> allowedContracts,
        List<string> toolSources)
    {
        var lines = frontmatter.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            i++;

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim().ToLowerInvariant();
            var rawValue = trimmed[(colon + 1)..].Trim();

            if (key is "starters" or "allowed_contracts" or "allowedcontracts" or "contracts" or "tool_sources" or "toolsources")
            {
                var target = key == "starters"
                    ? starters
                    : key is "tool_sources" or "toolsources"
                        ? toolSources
                        : allowedContracts;
                ReadListValues(lines, ref i, rawValue, target);
                continue;
            }

            string value;
            if (rawValue is "|" or ">" or "|-" or ">-")
            {
                var fold = rawValue[0] == '>';
                value = ReadBlockScalar(lines, ref i, fold);
            }
            else
            {
                value = Unquote(rawValue);
            }

            if (value.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "title":
                    title = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "icon":
                    icon = value;
                    break;
                case "category":
                    category = value;
                    break;
                case "handle":
                    handle = value;
                    break;
                case "memory_scope":
                case "memoryscope":
                    memoryScope = ParseMemoryScope(value);
                    break;
                case "launch":
                    if (string.Equals(value, "maximized", StringComparison.OrdinalIgnoreCase))
                    {
                        launch = AppLaunch.Maximized;
                    }
                    else if (string.Equals(value, "windowed", StringComparison.OrdinalIgnoreCase))
                    {
                        launch = AppLaunch.Windowed;
                    }

                    break;
            }
        }
    }

    private static void ReadListValues(string[] lines, ref int i, string rawValue, List<string> target)
    {
        if (rawValue.Length == 0)
        {
            while (i < lines.Length)
            {
                var itemTrimmed = lines[i].Trim();
                if (itemTrimmed.Length == 0 || itemTrimmed.StartsWith('#'))
                {
                    i++;
                    continue;
                }

                if (!itemTrimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                var item = Unquote(itemTrimmed[2..].Trim());
                if (item.Length > 0)
                {
                    target.Add(item);
                }

                i++;
            }
        }
        else if (rawValue.StartsWith('[') && rawValue.EndsWith(']'))
        {
            foreach (var item in ParseInlineArray(rawValue[1..^1]))
            {
                target.Add(item);
            }
        }
        else
        {
            var single = Unquote(rawValue);
            if (single.Length > 0)
            {
                target.Add(single);
            }
        }
    }

    private static MemoryScope ParseMemoryScope(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "none" => MemoryScope.None,
            "isolated" or "isolatedbotscope" or "isolated-bot-scope" or "isolated_bot_scope" => MemoryScope.IsolatedBotScope,
            _ => throw new InvalidDataException($"Unknown memory_scope '{value}'. Expected 'none' or 'isolated'."),
        };

    private static string ReadBlockScalar(string[] lines, ref int index, bool fold)
    {
        var blockLines = new List<string>();
        int? baseIndent = null;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                blockLines.Add(string.Empty);
                index++;
                continue;
            }

            var indent = CountLeadingSpaces(line);
            if (indent == 0)
            {
                break;
            }

            baseIndent ??= indent;
            var sliceStart = Math.Min(baseIndent.Value, indent);
            blockLines.Add(line[sliceStart..].TrimEnd());
            index++;
        }

        while (blockLines.Count > 0 && blockLines[^1].Length == 0)
        {
            blockLines.RemoveAt(blockLines.Count - 1);
        }

        if (blockLines.Count == 0)
        {
            return string.Empty;
        }

        if (!fold)
        {
            return string.Join('\n', blockLines).Trim();
        }

        var builder = new StringBuilder();
        foreach (var line in blockLines)
        {
            if (line.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append(' ');
            }

            builder.Append(line.Trim());
        }

        return builder.ToString().Trim();
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static List<string> ParseInlineArray(string inner)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var ch in inner)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\' && quote == '"')
            {
                escaped = true;
                current.Append(ch);
                continue;
            }

            if (quote is not null)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                var candidate = Unquote(current.ToString().Trim());
                if (candidate.Length > 0)
                {
                    items.Add(candidate);
                }

                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var tail = Unquote(current.ToString().Trim());
        if (tail.Length > 0)
        {
            items.Add(tail);
        }

        return items;
    }

    private static (string? FirstHeading, string? FirstParagraph, string[] BodyStarters) InspectBody(string instructions)
    {
        string? firstHeading = null;
        string? firstParagraph = null;
        var bodyStarters = new List<string>();
        var inCodeFence = false;
        var inStartersSection = false;

        foreach (var rawLine in instructions.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var headingText = trimmed.TrimStart('#').Trim();
                if (firstHeading is null && trimmed.StartsWith("# ", StringComparison.Ordinal) && headingText.Length > 0)
                {
                    firstHeading = headingText;
                }

                inStartersSection = IsStartersHeading(headingText);
                continue;
            }

            if (inStartersSection)
            {
                var starter = TryExtractBulletText(trimmed);
                if (starter is not null)
                {
                    bodyStarters.Add(starter);
                    continue;
                }
            }

            if (firstParagraph is null
                && !trimmed.StartsWith('-')
                && !trimmed.StartsWith('*')
                && !trimmed.StartsWith('|')
                && !trimmed.StartsWith('>'))
            {
                firstParagraph = trimmed.Length <= MaxDerivedDescriptionLength
                    ? trimmed
                    : trimmed[..(MaxDerivedDescriptionLength - 3)].TrimEnd() + "...";
            }
        }

        return (firstHeading, firstParagraph, [.. bodyStarters]);
    }

    private static bool IsStartersHeading(string heading)
        => heading.Equals("Starters", StringComparison.OrdinalIgnoreCase)
            || heading.Equals("Starter Prompts", StringComparison.OrdinalIgnoreCase)
            || heading.Equals("Example Prompts", StringComparison.OrdinalIgnoreCase)
            || heading.Equals("Prompts", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractBulletText(string trimmedLine)
    {
        string? raw = null;
        if (trimmedLine.StartsWith("- ", StringComparison.Ordinal) || trimmedLine.StartsWith("* ", StringComparison.Ordinal))
        {
            raw = trimmedLine[2..].Trim();
        }
        else
        {
            var dot = trimmedLine.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot <= 3 && trimmedLine[..dot].All(char.IsAsciiDigit))
            {
                raw = trimmedLine[(dot + 2)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var unquoted = Unquote(raw);
        if (unquoted.Length >= 2 && unquoted[0] == '`' && unquoted[^1] == '`')
        {
            unquoted = unquoted[1..^1].Trim();
        }

        return unquoted.Length > 0 ? unquoted : null;
    }

    private static string ResolveTitle(ParsedSkill parsed, string fallbackFileName)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Title))
        {
            return parsed.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.FirstHeading))
        {
            return parsed.FirstHeading.Trim();
        }

        var sourceName = !string.IsNullOrWhiteSpace(parsed.Name)
            ? parsed.Name
            : StripSkillSuffix(fallbackFileName);

        return HumanizeIdentifier(sourceName);
    }

    private static string ResolveDescription(ParsedSkill parsed, string fallbackTitle)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Description))
        {
            return parsed.Description.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.FirstParagraph))
        {
            return parsed.FirstParagraph.Trim();
        }

        return fallbackTitle;
    }

    private static string[] ResolveStarters(
        IReadOnlyList<string>? explicitStarters,
        string[] frontmatterStarters,
        string[] bodyStarters)
    {
        IEnumerable<string> source = explicitStarters is not null
            ? explicitStarters
            : frontmatterStarters.Length > 0
                ? frontmatterStarters
                : bodyStarters;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in source)
        {
            var cleaned = candidate?.Trim() ?? string.Empty;
            if (cleaned.Length > 0 && seen.Add(cleaned))
            {
                result.Add(cleaned);
            }
        }

        return [.. result];
    }

    private static string StripSkillSuffix(string rawName)
    {
        var value = (rawName ?? string.Empty).Trim().Replace('\\', '/');
        if (value.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^"/SKILL.md".Length];
        }
        else if (value.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        else if (value.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^".md".Length];
        }

        var lastSlash = value.LastIndexOf('/');
        return lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
    }

    private static string HumanizeIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var words = raw.Split(['-', '_', '/', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (word.Length == 1)
            {
                builder.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture));
            }
            else if (word.All(char.IsUpper))
            {
                builder.Append(word);
            }
            else
            {
                builder.Append(char.ToUpper(word[0], CultureInfo.InvariantCulture));
                builder.Append(word[1..]);
            }
        }

        return builder.ToString();
    }

    private static string ToSlug(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var previousDash = true;
        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousDash = false;
            }
            else if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            var inner = text[1..^1];
            return inner
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return text;
    }
}
