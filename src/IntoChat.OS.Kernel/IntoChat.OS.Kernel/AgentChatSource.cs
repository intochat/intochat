using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;

namespace IntoChat.OS.Kernel;

// The OS composes AI and UI. Neither module gains an implementation dependency on the other.
internal sealed class AgentChatSource(IGrainFactory grains) : IChatAgentSource
{
    public INeuron Source(string key) => grains.GetGrain<IAgent>(key);

    public async Task<ChatAgentSnapshot> Read(string key, long selectionRevision)
    {
        var state = await grains.GetGrain<IAgent>(key).Read();
        var runs = state.Runs.ToList();
        if (state.ActiveRun is { } active) { runs.RemoveAll(run => run.RunId == active.RunId); runs.Add(active); }
        var projection = new ChatProjection(key, selectionRevision, state.Version, [.. runs.SelectMany(Entries)])
        {
            Actions = state.ActiveRun is { } running
                ? [new("stop/" + running.RunId, "Stop", JsonSerializer.Serialize(new { runId = running.RunId }))] : [],
        };
        var latest = state.ActiveRun ?? state.Runs.LastOrDefault();
        return new(projection, latest is null ? null : new(key, selectionRevision, state.Version,
            latest.RunId, latest.Status switch
            {
                AgentRunStatus.Running => ChatRunStatus.Running,
                AgentRunStatus.Completed => ChatRunStatus.Completed,
                AgentRunStatus.Failed => ChatRunStatus.Failed,
                AgentRunStatus.Cancelled => ChatRunStatus.Cancelled,
                AgentRunStatus.Interrupted => ChatRunStatus.Interrupted,
                _ => throw new InvalidOperationException("Unknown agent run status."),
            }));
    }

    // A posted run (IAgent.Post, e.g. a greeting) has no user turn; any other run keeps its user
    // bubble, even an attachment-only turn with empty text.
    internal static IEnumerable<ChatEntry> Entries(AgentRun run)
    {
        if (!run.Posted) { yield return new ChatEntry(run.RunId + "/user", ChatRole.User, run.UserText); }
        yield return new ChatEntry(run.RunId + "/assistant", ChatRole.Assistant, run.Reply ?? "")
        {
            Status = run.Status.ToString().ToLowerInvariant(),
            Error = run.Error,
            Activities = [.. run.ToolCalls.Select(call => new ChatActivity(call.CallId, call.Name, call.Arguments, call.Result, call.Error))],
        };
    }
}
