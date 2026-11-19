using System.Text;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Agents;

// Auto-summarization and /compact reducer inspired by DurableChatHistoryProvider, HistorySummarizer, and ChatReducer:
// cuts transcripts cleanly on user-turn boundaries, preserves non-reducible high-signal turns (remember/approval/media),
// and summarizes evicted turns via the model catalog (when UseModelSummarizer is enabled) or deterministic structured extraction.
internal static class AgentHistoryCompactor
{
    internal const int MaxSummaryCharacters = 4_000;

    public static bool TryParseCompactCommand(string? userText, out string? focusInstructions)
    {
        focusInstructions = null;
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var trimmed = userText.Trim();
        if (string.Equals(trimmed, "/compact", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("/compact ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed["/compact ".Length..].Trim();
            focusInstructions = rest.Length > 0 ? rest : null;
            return true;
        }

        return false;
    }

    public static bool IsNonReducible(AiMessage message)
    {
        if (message.Content.Any(c => c is AiImage or AiAudio))
        {
            return true;
        }

        var text = ExtractText(message);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("remember", StringComparison.OrdinalIgnoreCase)
            || text.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || text.Contains("decision:", StringComparison.OrdinalIgnoreCase);
    }

    public static (AiMessage[] Messages, string? Summary) BoundWindow(
        AiMessage[] messages,
        AgentDefinition definition,
        long maxCharacters,
        string? existingSummary)
    {
        var effectiveMaxMessages = Math.Min(
            Math.Max(2, definition.MaxHistoryMessages),
            Math.Max(2, definition.CompactionThreshold));
        var recentWindow = Math.Clamp(definition.CompactionRecentWindow, 1, effectiveMaxMessages);

        var totalChars = messages.Sum(MessageSize);
        if (messages.Length <= effectiveMaxMessages && totalChars <= maxCharacters)
        {
            return (messages, existingSummary);
        }

        var targetKeep = messages.Length > definition.CompactionThreshold
            ? Math.Min(recentWindow, effectiveMaxMessages)
            : effectiveMaxMessages;

        var drop = Math.Max(0, messages.Length - targetKeep);
        var remainingSize = messages.Skip(drop).Sum(MessageSize);
        while (drop < messages.Length && remainingSize > maxCharacters)
        {
            remainingSize -= MessageSize(messages[drop]);
            drop++;
        }

        if (drop == 0)
        {
            return (messages, existingSummary);
        }

        drop = AlignToUserTurnBoundary(messages, drop);
        var evicted = messages[..drop];
        var kept = BuildKeptMessages(evicted, messages[drop..], maxCharacters);
        var summary = BuildDeterministicSummary(evicted, existingSummary, focusInstructions: null);
        return (kept, summary);
    }

    public static async Task<(AiMessage[] Messages, string Summary, int EvictedCount)> CompactAsync(
        AiMessage[] messages,
        AgentDefinition definition,
        string? existingSummary,
        string? focusInstructions,
        bool force,
        string scope,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct)
    {
        if (messages.Length == 0)
        {
            return ([], existingSummary ?? "", 0);
        }

        int drop;
        if (force)
        {
            // Explicit /compact keeps at most the final user+assistant exchange when >= 6 messages exist;
            // on short transcripts it folds the entire transcript into the summary so compaction is immediate.
            if (messages.Length <= 4)
            {
                drop = messages.Length;
            }
            else
            {
                var lastUserIdx = FindLastUserTurnStart(messages);
                drop = lastUserIdx > 0 ? lastUserIdx : messages.Length;
            }
        }
        else
        {
            var effectiveThreshold = Math.Min(Math.Max(2, definition.MaxHistoryMessages), Math.Max(2, definition.CompactionThreshold));
            if (messages.Length <= effectiveThreshold && messages.Sum(MessageSize) <= AgentNeuron.MaxHistoryCharacters)
            {
                return (messages, existingSummary ?? "", 0);
            }

            var recentWindow = Math.Clamp(definition.CompactionRecentWindow, 1, effectiveThreshold);
            drop = AlignToUserTurnBoundary(messages, Math.Max(1, messages.Length - recentWindow));
        }

        var evicted = messages[..drop];
        var kept = drop >= messages.Length
            ? []
            : BuildKeptMessages(evicted, messages[drop..], AgentNeuron.MaxHistoryCharacters);

        string? summary = null;
        if (definition.UseModelSummarizer && evicted.Length > 0)
        {
            summary = await TrySummarizeWithModelAsync(evicted, existingSummary, focusInstructions, definition.Model, scope, services, logger, ct)
                .ConfigureAwait(false);
        }

        summary ??= BuildDeterministicSummary(evicted, existingSummary, focusInstructions) ?? existingSummary ?? "";
        return (kept, ClampSummary(summary), evicted.Length);
    }

    internal static string? BuildDeterministicSummary(
        AiMessage[] evicted,
        string? existingSummary,
        string? focusInstructions)
    {
        if (evicted.Length == 0)
        {
            return existingSummary;
        }

        var userPrompts = new List<string>();
        var pinnedItems = new List<string>();
        var outcomes = new List<string>();

        foreach (var msg in evicted)
        {
            var text = CollapseWhitespace(ExtractText(msg));
            if (text.Length == 0)
            {
                continue;
            }

            if (msg.Role == "user")
            {
                userPrompts.Add(Truncate(text, 140));
                if (IsNonReducible(msg))
                {
                    pinnedItems.Add(Truncate(text, 160));
                }
            }
            else if (msg.Role == "assistant")
            {
                outcomes.Add(Truncate(text, 160));
                if (IsNonReducible(msg))
                {
                    pinnedItems.Add(Truncate(text, 160));
                }
            }
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.Append(existingSummary.Trim());
            sb.Append(' ');
        }

        if (!string.IsNullOrWhiteSpace(focusInstructions))
        {
            sb.Append("[Focus: ").Append(Truncate(CollapseWhitespace(focusInstructions), 120)).Append("] ");
        }

        if (userPrompts.Count > 0)
        {
            sb.Append("Goals: ").Append(string.Join(" | ", userPrompts.TakeLast(8))).Append(". ");
        }

        if (pinnedItems.Count > 0)
        {
            sb.Append("Pinned: ").Append(string.Join(" | ", pinnedItems.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(6))).Append(". ");
        }

        if (outcomes.Count > 0)
        {
            sb.Append("Outcomes: ").Append(string.Join(" | ", outcomes.TakeLast(4))).Append('.');
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? existingSummary : ClampSummary(result);
    }

    private static async Task<string?> TrySummarizeWithModelAsync(
        AiMessage[] evicted,
        string? existingSummary,
        string? focusInstructions,
        ModelSelection? modelSelection,
        string scope,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct)
    {
        var catalog = services.GetService<ModelCatalog>();
        if (catalog is null)
        {
            return null;
        }

        var transcriptLines = evicted
            .Select(m => (m.Role, Text: CollapseWhitespace(ExtractText(m))))
            .Where(x => x.Text.Length > 0)
            .Select(x => $"{x.Role}: {Truncate(x.Text, 400)}")
            .ToArray();
        if (transcriptLines.Length == 0)
        {
            return null;
        }

        var focusClause = string.IsNullOrWhiteSpace(focusInstructions)
            ? ""
            : $"\nFocus instructions: {focusInstructions.Trim()}";
        var prompt = $"""
            Summarize this conversation history concisely, preserving key goals, decisions, deliverables, tool outcomes, and open threads.
            Do not include greetings or small talk.{focusClause}

            Conversation:
            {string.Join('\n', transcriptLines)}
            """;

        var chatMessages = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            chatMessages.Add(new ChatMessage(ChatRole.System, $"Previous summary: {existingSummary}"));
        }
        chatMessages.Add(new ChatMessage(ChatRole.User, prompt));

        try
        {
            var resolved = await catalog.Resolve(scope, modelSelection, requiresTools: false, ct).ConfigureAwait(false);
            using var client = await catalog.CreateClient(scope, resolved, ct).ConfigureAwait(false);
            var response = await client.GetResponseAsync(chatMessages, cancellationToken: ct).ConfigureAwait(false);
            var summaryText = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(summaryText) ? null : ClampSummary(summaryText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Model history summarization failed; falling back to deterministic compaction.");
            return null;
        }
    }

    private static AiMessage[] BuildKeptMessages(AiMessage[] evicted, AiMessage[] recent, long maxCharacters)
    {
        // Preserve non-reducible user messages (e.g. explicit "remember" / "approval" / media) as evicted-image-stripped user context
        // without breaking tool-call/tool-result pairing.
        var preserved = evicted
            .Where(m => m.Role == "user" && IsNonReducible(m))
            .Select(EvictMediaToText)
            .TakeLast(4)
            .ToList();

        var combined = new List<AiMessage>(preserved.Count + recent.Length);
        combined.AddRange(preserved);
        combined.AddRange(recent);

        var total = combined.Sum(MessageSize);
        while (total > maxCharacters && preserved.Count > 0)
        {
            total -= MessageSize(combined[0]);
            combined.RemoveAt(0);
            preserved.RemoveAt(0);
        }

        return [.. combined];
    }

    private static AiMessage EvictMediaToText(AiMessage message)
    {
        if (!message.Content.Any(c => c is AiImage or AiAudio))
        {
            return message;
        }

        var parts = message.Content.Select<AiContent, AiContent>(part => part switch
        {
            AiImage img => new AiText($"[Image: {img.MediaType}]"),
            AiAudio aud => new AiText($"[Audio: {aud.MediaType}]"),
            _ => part,
        }).ToArray();

        return new AiMessage(message.Role, parts);
    }

    private static int AlignToUserTurnBoundary(AiMessage[] messages, int drop)
    {
        while (drop < messages.Length && messages[drop].Role != "user")
        {
            drop++;
        }
        return drop;
    }

    private static int FindLastUserTurnStart(AiMessage[] messages)
    {
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user")
            {
                return i;
            }
        }
        return 0;
    }

    internal static long MessageSize(AiMessage message) => message.Content.Sum(content => (long)(content switch
    {
        AiText text => text.Text.Length,
        AiReasoning reasoning => reasoning.Text.Length,
        AiToolCall call => call.Name.Length + call.ArgumentsJson.Length,
        AiToolResult result => result.ResultJson.Length,
        _ => 0,
    }));

    private static string ExtractText(AiMessage message)
        => string.Concat(message.Content.OfType<AiText>().Select(t => t.Text));

    private static string CollapseWhitespace(string text)
        => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string ClampSummary(string summary)
        => summary.Length <= MaxSummaryCharacters ? summary : summary[^MaxSummaryCharacters..];
}
