using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Modules.Apps.Flows;

public sealed record LoopFinding(string Key, string Source, string Title, string Detail);

public sealed record LoopTraceSpan(
    string TraceId,
    string SpanId,
    string Resource,
    string Name,
    string Status,
    long DurationMicroseconds,
    string? Error = null);

public static class LoopFindingSources
{
    public static IReadOnlyList<LoopFinding> FromErrorTraces(IReadOnlyList<LoopTraceSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return [];
        }

        var grouped = new Dictionary<string, (string Resource, string Name, string Error, int Count)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var span in spans)
        {
            if (span is null)
            {
                continue;
            }

            var isError = !string.IsNullOrWhiteSpace(span.Error)
                || string.Equals(span.Status, "Error", StringComparison.OrdinalIgnoreCase);
            if (!isError)
            {
                continue;
            }

            var resource = string.IsNullOrWhiteSpace(span.Resource) ? "unknown" : span.Resource.Trim();
            var name = string.IsNullOrWhiteSpace(span.Name) ? "unknown" : span.Name.Trim();
            var key = $"trace:{resource}:{name}";
            var errorText = string.IsNullOrWhiteSpace(span.Error) ? span.Status : span.Error.Trim();

            if (grouped.TryGetValue(key, out var existing))
            {
                grouped[key] = (existing.Resource, existing.Name, existing.Error, existing.Count + 1);
            }
            else
            {
                grouped[key] = (resource, name, errorText, 1);
                order.Add(key);
            }
        }

        var findings = new List<LoopFinding>(order.Count);
        foreach (var key in order)
        {
            var entry = grouped[key];
            var detail = entry.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $"{entry.Error} (seen {entry.Count} times)")
                : entry.Error;
            findings.Add(new LoopFinding(
                Key: key,
                Source: "trace",
                Title: $"{entry.Resource}: {entry.Name}",
                Detail: detail));
        }

        return findings;
    }

    public static IReadOnlyList<LoopFinding> FromOwedMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var findings = new List<LoopFinding>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("* ", StringComparison.Ordinal))
            {
                continue;
            }

            var content = line[2..].Trim();
            var ownerIndex = content.LastIndexOf("Owner:", StringComparison.OrdinalIgnoreCase);
            if (ownerIndex < 0)
            {
                continue;
            }

            var owner = content[(ownerIndex + "Owner:".Length)..].Trim().TrimEnd('.', ',', ';');
            if (!string.Equals(owner, "loop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var body = content[..ownerIndex].Trim();
            if (body.Length == 0)
            {
                continue;
            }

            var key = $"owed:{StableHash(body)}";
            if (!seenKeys.Add(key))
            {
                continue;
            }

            var title = body.Length <= 80 ? body : body[..80].TrimEnd() + "...";
            findings.Add(new LoopFinding(
                Key: key,
                Source: "owed",
                Title: title,
                Detail: body));
        }

        return findings;
    }

    public static IReadOnlyList<LoopFinding> FromFailingFacts(IReadOnlyList<(string FactName, string Detail)> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            return [];
        }

        var findings = new List<LoopFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (factName, detail) in failures)
        {
            if (string.IsNullOrWhiteSpace(factName))
            {
                continue;
            }

            var trimmedName = factName.Trim();
            var key = $"fact:{trimmedName}";
            if (!seen.Add(key))
            {
                continue;
            }

            findings.Add(new LoopFinding(
                Key: key,
                Source: "fact",
                Title: trimmedName,
                Detail: string.IsNullOrWhiteSpace(detail) ? "Test failure" : detail.Trim()));
        }

        return findings;
    }

    private static string StableHash(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes, 0, 6);
    }
}

public sealed class FindingDeduplicator
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdmit(LoopFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.Key);

        lock (_gate)
        {
            return _seen.Add(finding.Key.Trim());
        }
    }

    public bool HasSeen(string findingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);

        lock (_gate)
        {
            return _seen.Contains(findingKey.Trim());
        }
    }

    public IReadOnlyList<LoopFinding> Admit(IEnumerable<LoopFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var admitted = new List<LoopFinding>();
        lock (_gate)
        {
            foreach (var finding in findings)
            {
                if (finding is null || string.IsNullOrWhiteSpace(finding.Key))
                {
                    continue;
                }

                if (_seen.Add(finding.Key.Trim()))
                {
                    admitted.Add(finding);
                }
            }
        }

        return admitted;
    }
}
