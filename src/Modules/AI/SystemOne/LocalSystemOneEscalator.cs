using System.Globalization;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.SystemOne;

// Local System-2 fallback for SystemOneNeuron when VENN abstains and no hosted TypeSafe
// endpoint is configured. Uses the brain's already-loaded chat model (e.g. gemma4:12b)
// with a compact JSON-only decision prompt so System One works out-of-the-box on Day 1.
internal static class LocalSystemOneEscalator
{
    private const string SystemPrompt =
        "You are a fast, deterministic decision classifier. "
        + "Return ONLY a single JSON object mapping each question key to its answer. "
        + "Do not include markdown fences or explanations. "
        + "For choice questions return {\"choice\":\"<exact option key>\",\"confidence\":0.0-1.0}. "
        + "For score questions return {\"score\":0.0-1.0,\"confidence\":0.0-1.0}. "
        + "For noul questions return {\"noul\":0.0-1.0}.";

    internal static async Task<SystemOneResult?> TryAskAsync(
        ModelCatalog? catalog,
        string brainId,
        SystemOneRequest request,
        CancellationToken ct)
    {
        if (catalog is null || request.Questions.Count == 0)
        {
            return null;
        }

        ResolvedModel resolved;
        IChatClient client;
        try
        {
            resolved = await catalog.Decide(brainId, Role.Chat, requiresTools: false, ct: ct).ConfigureAwait(false);
            if (resolved.Provider == AiProvider.Device)
            {
                return null;
            }
            client = await catalog.CreateClient(resolved, ct, invokesTools: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NoProviderConfiguredException or ProviderUnavailableException or InvalidOperationException)
        {
            return null;
        }

        var prompt = BuildPrompt(request);
        var options = new ChatOptions
        {
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.Json,
        };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, prompt)],
            options,
            ct).ConfigureAwait(false);

        var raw = response.Text ?? string.Empty;
        var answers = ParseAnswers(raw, request.Questions);
        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
        return new SystemOneResult(resolved.Model, answers, inputTokens, outputTokens);
    }

    internal static string BuildPrompt(SystemOneRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("State:");
        sb.AppendLine(request.State);
        sb.AppendLine();
        sb.AppendLine("Questions:");
        foreach (var (key, question) in request.Questions)
        {
            sb.Append("- ").Append(key).Append(": ");
            switch (question)
            {
                case SystemOneQuestion.Choice choice:
                    sb.Append("choice | ").Append(choice.Instructions).Append(" | options: ");
                    var first = true;
                    foreach (var (opt, desc) in choice.Criteria)
                    {
                        if (!first) { sb.Append(", "); }
                        first = false;
                        sb.Append(opt);
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            sb.Append(" (").Append(desc).Append(')');
                        }
                    }
                    sb.AppendLine();
                    break;
                case SystemOneQuestion.Score score:
                    sb.Append("score | ").Append(score.Instructions);
                    if (score.Criteria.Length > 0)
                    {
                        sb.Append(" | criteria: ").Append(string.Join(", ", score.Criteria));
                    }
                    sb.AppendLine();
                    break;
                case SystemOneQuestion.Noul noul:
                    sb.Append("noul | ").AppendLine(noul.Instructions);
                    break;
            }
        }
        return sb.ToString();
    }

    internal static Dictionary<string, SystemOneAnswer> ParseAnswers(
        string raw,
        IReadOnlyDictionary<string, SystemOneQuestion> questions)
    {
        var result = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var (key, question) in questions)
            {
                if (!doc.RootElement.TryGetProperty(key, out var elem))
                {
                    continue;
                }

                switch (question)
                {
                    case SystemOneQuestion.Choice choice:
                        {
                            var picked = elem.ValueKind == JsonValueKind.String
                                ? elem.GetString()
                                : TryGetString(elem, "choice") ?? TryGetString(elem, "value");
                            if (string.IsNullOrWhiteSpace(picked) || !choice.Criteria.ContainsKey(picked))
                            {
                                continue;
                            }
                            var conf = Math.Clamp(TryGetDouble(elem, "confidence") ?? 0.75, 0.0, 1.0);
                            result[key] = new SystemOneAnswer.Choice(
                                picked,
                                new Dictionary<string, double>(StringComparer.Ordinal) { [picked] = conf },
                                conf);
                            break;
                        }
                    case SystemOneQuestion.Score:
                        {
                            var val = elem.ValueKind == JsonValueKind.Number
                                ? elem.GetDouble()
                                : TryGetDouble(elem, "score") ?? TryGetDouble(elem, "value");
                            if (val is null)
                            {
                                continue;
                            }
                            var conf = Math.Clamp(TryGetDouble(elem, "confidence") ?? 0.75, 0.0, 1.0);
                            result[key] = new SystemOneAnswer.Score(val.Value, [], conf);
                            break;
                        }
                    case SystemOneQuestion.Noul:
                        {
                            var val = elem.ValueKind == JsonValueKind.Number
                                ? elem.GetDouble()
                                : elem.ValueKind == JsonValueKind.True ? 1.0
                                : elem.ValueKind == JsonValueKind.False ? 0.0
                                : TryGetDouble(elem, "noul") ?? TryGetDouble(elem, "value");
                            if (val is null)
                            {
                                continue;
                            }
                            result[key] = new SystemOneAnswer.Noul(Math.Clamp(val.Value, 0.0, 1.0));
                            break;
                        }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed LLM output simply leaves abstained keys unanswered.
        }

        return result;
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static string? TryGetString(JsonElement elem, string prop)
        => elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? TryGetDouble(JsonElement elem, string prop)
    {
        if (elem.ValueKind != JsonValueKind.Object || !elem.TryGetProperty(prop, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
        {
            return d;
        }
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
