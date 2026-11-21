using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.AI.Conversations;

internal static class LegacyImport
{
    public static (ConversationEntry[] Entries, AgentRun[] Runs) From(AgentState state, AiMessage[] transcript, AccountId owner, string agentId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(transcript);

        var person = new Participant.Human(owner);
        var agent = new Participant.Assistant(agentId);

        // Transcript is only a fallback for tool results missing on the run record; it never overrides the run.
        var transcriptResults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var msg in transcript)
        {
            foreach (var content in msg.Content)
            {
                if (content is AiToolResult result && !string.IsNullOrEmpty(result.CallId))
                {
                    transcriptResults.TryAdd(result.CallId, result.ResultJson);
                }
            }
        }

        var entries = new List<ConversationEntry>();
        var usedIds = new HashSet<EntryId>();
        var lastAt = DateTimeOffset.MinValue;

        void AddEntry(ConversationEntry entry)
        {
            if (usedIds.Add(entry.Id))
            {
                entries.Add(entry);
            }
        }

        foreach (var run in state.Runs)
        {
            var runStart = run.StartedAt >= lastAt ? run.StartedAt : lastAt;
            lastAt = runStart;

            if (run.Posted)
            {
                var postId = run.RunId.StartsWith("post/", StringComparison.Ordinal)
                    ? run.RunId["post/".Length..]
                    : run.RunId;
                AddEntry(new Said(EntryIds.Post(postId), agent, runStart, run.Reply ?? string.Empty));
                continue;
            }

            AddEntry(new Said(EntryIds.Said(run.RunId), person, runStart, run.UserText ?? string.Empty));

            foreach (var call in run.ToolCalls)
            {
                var callId = string.IsNullOrEmpty(call.CallId) ? Guid.NewGuid().ToString("N") : call.CallId;
                AddEntry(new Called(EntryIds.Call(run.RunId, callId), agent, lastAt, call.Name, call.Arguments ?? "{}"));

                string? resultJson = call.Result;
                if (resultJson is null && transcriptResults.TryGetValue(callId, out var fromTranscript))
                {
                    resultJson = fromTranscript;
                }
                if (resultJson is null && (call.Code is not null || call.Error is not null))
                {
                    resultJson = TurnRunner.SerializeToolError(call.Code ?? "tool_error", call.Error ?? "The tool call failed.");
                }

                if (resultJson is not null)
                {
                    var ok = call.Code is null && call.Error is null;
                    var card = ok ? ConversationCards.FromResult(resultJson) : null;
                    AddEntry(new Returned(EntryIds.Result(run.RunId, callId), agent, lastAt, call.Name, resultJson, ok, card));
                }
            }

            if (run.Status == AgentRunStatus.Completed)
            {
                var endAt = run.EndedAt is { } ended && ended >= lastAt ? ended : lastAt;
                lastAt = endAt;
                ModelId? model = run.Model is { Model.Length: > 0 } m ? new ModelId(m.Model) : null;
                AddEntry(new Said(EntryIds.Reply(run.RunId), agent, endAt, run.Reply ?? string.Empty, model));
            }
        }

        if (!string.IsNullOrWhiteSpace(state.Summary))
        {
            var summaryId = EntryIds.Summary("legacy");
            var firstAt = entries.Count > 0 ? entries[0].At : DateTimeOffset.MinValue;
            var upTo = entries.Count > 0 ? entries[0].Id : summaryId;
            entries.Insert(0, new Summarized(summaryId, agent, firstAt, state.Summary, upTo));
        }

        return ([.. entries], [.. state.Runs]);
    }
}
