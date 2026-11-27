using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Ino;

// Extracts InoCards from tool call JSON results and inline live Slot(neuronUri) attachments from assistant prose.
internal static partial class InoCards
{
    public static InoCard[] FromToolCalls(IEnumerable<AgentToolCall> calls)
        => [.. calls.Select(c => c.Result is { Length: > 0 } r ? FromResult(r) : null).OfType<InoCard>()];

    public static (string CleanText, IReadOnlyList<NeuronRef> Slots) ExtractInlineSlots(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText) || !rawText.Contains("slot:", StringComparison.OrdinalIgnoreCase))
        {
            return (rawText ?? string.Empty, []);
        }

        var slots = new List<NeuronRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Capture(Match m)
        {
            if (ToLiveSlot(m.Groups["target"].Value) is { } s && seen.Add($"{s.Kind}:{s.NeuronUri}")) { slots.Add(s); }
            return string.Empty;
        }

        var cleaned = UriSlotRegex().Replace(BracketSlotRegex().Replace(rawText, Capture), Capture);
        return slots.Count == 0 ? (rawText, []) : (cleaned.Trim(), slots);
    }

    public static NeuronRef? ToLiveSlot(string? rawTarget, string? explicitKind = null)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) { return null; }
        var trimmed = rawTarget.Trim().TrimEnd('.', ',', ';', ')');
        if (trimmed.StartsWith("slot://", StringComparison.OrdinalIgnoreCase)) { trimmed = trimmed[7..]; }

        var refKind = explicitKind;
        foreach (var (prefix, kind) in (ReadOnlySpan<(string, string)>)[("app:", "app"), ("unit:", "unit"), ("view:", "view")])
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                refKind ??= kind;
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        var uri = InoView.StripNeuronUri(trimmed).Trim('/');
        if (uri.IndexOf('#') is > 0 and var hash) { uri = uri[..hash]; }
        if (string.IsNullOrWhiteSpace(uri)) { return null; }

        var resolved = !string.IsNullOrWhiteSpace(refKind) ? refKind
            : uri.StartsWith("unit.", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("units/", StringComparison.OrdinalIgnoreCase) ? "unit"
            : !uri.Contains('/', StringComparison.Ordinal) && !uri.Contains('.', StringComparison.Ordinal) ? "app"
            : "view";
        if (resolved == "view" && uri.StartsWith("apps/", StringComparison.OrdinalIgnoreCase) && !uri.EndsWith("/view", StringComparison.OrdinalIgnoreCase))
        {
            uri = $"{uri}/view";
        }

        return Embed(uri, resolved);
    }

    internal static InoCard? FromResult(string result)
    {
        try
        {
            using var payload = JsonDocument.Parse(result);
            return payload.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                ? card.Deserialize(InoJson.Default.InoCard)
                : null;
        }
        catch (JsonException) { return null; }
    }

    internal static InoDrawerArtifact? FromCodeOrchestrationResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) { return null; }
        try
        {
            var view = JsonSerializer.Deserialize<CodeOrchestrationView>(resultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return view is { FlowId.Length: > 0 } ? FromCodeOrchestrationView(view) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static InoDrawerArtifact FromCodeOrchestrationView(CodeOrchestrationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var status = view.Status switch
        {
            FlowStatus.Completed => "done",
            FlowStatus.Failed or FlowStatus.Cancelled => "failed",
            _ => "running",
        };

        var diffSb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(view.Result?.Summary))
        {
            diffSb.AppendLine(view.Result.Summary);
        }

        if (view.Result?.Artifacts is { Length: > 0 } artifacts)
        {
            foreach (var item in artifacts)
            {
                diffSb.AppendLine();
                diffSb.Append(item.Name).Append(" (").Append(item.Kind).AppendLine(")");
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    diffSb.AppendLine(item.Content);
                }
                else if (!string.IsNullOrWhiteSpace(item.Uri))
                {
                    diffSb.AppendLine(item.Uri);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(view.CompiledScript))
        {
            if (diffSb.Length > 0) { diffSb.AppendLine(); }
            diffSb.AppendLine(view.CompiledScript);
        }

        var logsSb = new StringBuilder();
        logsSb.Append("Status: ").AppendLine(view.Status.ToString());
        if (!string.IsNullOrWhiteSpace(view.ExecutionTarget))
        {
            logsSb.Append("Execution target: ").AppendLine(view.ExecutionTarget);
        }

        if (!string.IsNullOrWhiteSpace(view.ExecutedByMachineId))
        {
            logsSb.Append("Machine: ").AppendLine(view.ExecutedByMachineId);
        }

        if (view.ScopedContracts is { Length: > 0 })
        {
            logsSb.Append("Scoped contracts: ").AppendLine(string.Join(", ", view.ScopedContracts));
        }

        if (view.Result?.Metrics is { Length: > 0 } metrics)
        {
            foreach (var metric in metrics)
            {
                var unit = string.IsNullOrWhiteSpace(metric.Unit) ? "" : $" {metric.Unit}";
                logsSb.Append("Metric · ").Append(metric.Name).Append(": ").Append(metric.Value).AppendLine(unit);
            }
        }

        if (!string.IsNullOrWhiteSpace(view.Error))
        {
            logsSb.Append("Error: ").AppendLine(view.Error);
        }

        return new InoDrawerArtifact(
            Id: $"code-orch:{view.FlowId}",
            Title: string.IsNullOrWhiteSpace(view.Title) ? "Code Orchestration" : view.Title,
            Kind: "code-orchestration",
            Status: status,
            FlowId: view.FlowId,
            LiveViewPath: view.LiveViewPath,
            CompiledScript: view.CompiledScript,
            Summary: view.Result?.Summary ?? view.Error,
            DiffText: diffSb.Length > 0 ? diffSb.ToString().Trim() : null,
            LogsText: logsSb.Length > 0 ? logsSb.ToString().Trim() : null,
            ScopedContracts: view.ScopedContracts ?? [],
            PinnedAppAlias: view.PinnedAppAlias,
            ExecutionTarget: view.ExecutionTarget);
    }

    [GeneratedRegex(@"\[slot:(?<target>[^\]\s]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketSlotRegex();

    [GeneratedRegex(@"(?<!\w)slot://(?<target>[^\s\]\)"">]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriSlotRegex();
}
