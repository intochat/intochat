using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class InoNativeHarness
{
    internal static async Task<InoHydratedBrainSnapshot> HydrateBrainContextAsync(
        BrainId? brain,
        string repositoryId,
        string goal,
        IWorkingMemory? workingMemory,
        IUiIntrospector? uiIntrospector,
        IKnowledgeStore? knowledgeStore,
        ILogger logger,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? matchedBriefKey = null;

        if (workingMemory is not null)
        {
            try
            {
                var pack = await workingMemory.ReadStartupPack().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(pack.Soul))
                {
                    sb.AppendLine($"SOUL: {CollapseSingleLine(pack.Soul, 320)}");
                }

                if (!string.IsNullOrWhiteSpace(pack.User))
                {
                    sb.AppendLine($"USER: {CollapseSingleLine(pack.User, 260)}");
                }

                if (!string.IsNullOrWhiteSpace(pack.Project))
                {
                    sb.AppendLine($"PROJECT: {CollapseSingleLine(pack.Project, 420)}");
                }

                if (!pack.Scratchpad.IsEmpty)
                {
                    var scratchBullets = pack.Scratchpad.ActiveThreads
                        .Concat(pack.Scratchpad.EnvironmentNotes)
                        .Concat(pack.Scratchpad.PendingDecisions)
                        .Where(static s => !string.IsNullOrWhiteSpace(s))
                        .Take(4)
                        .Select(static s => CollapseSingleLine(s, 140));
                    sb.AppendLine("WORKING_SCRATCHPAD: " + string.Join(" | ", scratchBullets));
                }

                var candidateBrief = SelectMatchingProjectBrief(pack.AvailableBriefs, repositoryId, goal);
                if (candidateBrief is not null)
                {
                    var brief = await workingMemory.GetBrief(candidateBrief.Key).ConfigureAwait(false);
                    if (brief is not null && !string.IsNullOrWhiteSpace(brief.Content))
                    {
                        matchedBriefKey = brief.Key;
                        sb.AppendLine($"BRIEF[{brief.Key}]: {CollapseSingleLine(brief.Content, 800)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IWorkingMemory for brain '{brain}'");
            }
        }

        if (uiIntrospector is not null)
        {
            try
            {
                var screens = await uiIntrospector.InspectOpenScreens().ConfigureAwait(false);
                var graphs = await uiIntrospector.ListStateGraphs().ConfigureAwait(false);
                if (screens.Length > 0 || graphs.Length > 0)
                {
                    var apps = string.Join(", ", screens.Take(8).Select(static s => s.AppId));
                    var healthyCount = graphs.Count(static g => g.IsHealthy);
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"UI_INTROSPECTOR: {screens.Length} open screens ({apps}); {healthyCount}/{graphs.Length} healthy StateGraphs."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IUiIntrospector for brain '{brain}'");
            }
        }

        if (knowledgeStore is not null)
        {
            try
            {
                var hits = await knowledgeStore.SemanticSearchAsync($"{repositoryId} {goal}", limit: 4, ct).ConfigureAwait(false);
                if (hits.Count > 0)
                {
                    var facts = string.Join(" | ", hits.Select(static h => $"{h.Node.Label} ({h.Node.Type})"));
                    sb.AppendLine($"KNOWLEDGE_GRAPH: {CollapseSingleLine(facts, 420)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogSwallowed(ex, $"hydrating IKnowledgeStore for brain '{brain}'");
            }
        }

        var compact = TruncateUtf8(sb.ToString().Trim(), MaxHydratedContextBytes);
        var bytes = Encoding.UTF8.GetByteCount(compact);
        return new InoHydratedBrainSnapshot(compact, matchedBriefKey, bytes);
    }

    internal static async Task RecordMilestoneBestEffortAsync(
        ISessionJournal journal,
        string? conversationOrFlowId,
        string goal,
        string? projectBriefKey,
        string[] deliverables,
        string[] decisions,
        string[]? openThreads,
        string[]? resolvedThreads,
        ILogger logger)
    {
        try
        {
            await journal.RecordMilestone(new SessionMilestone
            {
                ConversationId = string.IsNullOrWhiteSpace(conversationOrFlowId) ? null : conversationOrFlowId,
                Goal = goal,
                ProjectBrief = projectBriefKey,
                Deliverables = deliverables,
                Decisions = decisions,
                OpenThreads = openThreads,
                ResolvedThreads = resolvedThreads,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogSwallowed(ex, $"recording SessionMilestone for '{conversationOrFlowId}'");
        }
    }


    private IWorkingMemory? ResolveWorkingMemory(BrainId? brain)
    {
        if (workingMemory is not null)
        {
            return workingMemory;
        }

        if (services?.GetService<IWorkingMemory>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(b));
        }

        return null;
    }

    private ISessionJournal? ResolveSessionJournal(BrainId? brain)
    {
        if (sessionJournal is not null)
        {
            return sessionJournal;
        }

        if (services?.GetService<ISessionJournal>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return proxies.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(b));
        }

        return null;
    }

    private IUiIntrospector? ResolveUiIntrospector(BrainId? brain)
    {
        if (uiIntrospector is not null)
        {
            return uiIntrospector;
        }

        if (services?.GetService<IUiIntrospector>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return OptionalProxy(() => proxies.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(b)), "optional ui introspector is not hosted here");
        }

        return null;
    }

    private IKnowledgeStore? ResolveKnowledgeStore(BrainId? brain)
    {
        if (knowledgeStore is not null)
        {
            return knowledgeStore;
        }

        if (services?.GetService<IKnowledgeStore>() is { } direct)
        {
            return direct;
        }

        if (brain is { } b && services?.GetService<INeuronProxies>() is { } proxies)
        {
            return OptionalProxy(() => proxies.Get<IKnowledgeStore>(new NeuronId(b, "ai.knowledge-store", KnowledgeStoreKey)), "optional knowledge store is not hosted here");
        }

        return null;
    }

    // The introspector and knowledge store are optional brain context: a host that does not run their module
    // (an in-process TestOS refuses an unregistered type at Get) leaves the harness without them.
    private T? OptionalProxy<T>(Func<T> get, string what) where T : class
    {
        try
        {
            return get();
        }
        catch (InvalidOperationException error)
        {
            _logger.LogSwallowed(error, what);
            return null;
        }
    }

    internal static ContextBriefSummary? SelectMatchingProjectBrief(
        IReadOnlyList<ContextBriefSummary>? briefs,
        string repositoryId,
        string goal)
    {
        if (briefs is null || briefs.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            var repoTag = $"repo:{repositoryId.Trim()}";
            foreach (var brief in briefs)
            {
                if (string.Equals(brief.Key, repoTag, StringComparison.OrdinalIgnoreCase))
                {
                    return brief;
                }
            }
        }

        foreach (var brief in briefs)
        {
            if (!string.IsNullOrWhiteSpace(repositoryId)
                && (brief.Key.Contains(repositoryId, StringComparison.OrdinalIgnoreCase)
                    || brief.Title.Contains(repositoryId, StringComparison.OrdinalIgnoreCase)))
            {
                return brief;
            }
        }

        foreach (var brief in briefs)
        {
            if (!string.IsNullOrWhiteSpace(goal)
                && brief.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(word => word.Length >= 4 && goal.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return brief;
            }
        }

        return briefs.FirstOrDefault(static b => string.Equals(b.Category, "project", StringComparison.OrdinalIgnoreCase))
            ?? briefs[0];
    }

    private static OperationProgressed[] SnapshotProgress(List<OperationProgressed> events, Lock gate)
    {
        lock (gate)
        {
            return [.. events];
        }
    }

    internal static string CollapseSingleLine(string text, int maxChars)
    {
        var single = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return single.Length <= maxChars ? single : single[..maxChars].TrimEnd() + "...";
    }

    internal static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var builder = new StringBuilder();
        var used = 0;
        const string Suffix = "...";
        var budget = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(Suffix));

        foreach (var rune in text.EnumerateRunes())
        {
            var runeStr = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeStr);
            if (used + runeBytes > budget)
            {
                break;
            }

            builder.Append(runeStr);
            used += runeBytes;
        }

        return builder.Append(Suffix).ToString();
    }

    private sealed record SliceExecutionOutcome(
        bool Succeeded,
        EngineerTaskSlice UpdatedSlice,
        string[] TouchedFiles,
        string Summary,
        string ModelId,
        int SemanticErrorCount,
        string? FailureDetail);
}
