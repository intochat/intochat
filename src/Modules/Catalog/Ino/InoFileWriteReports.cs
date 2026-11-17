using DigitalBrain.Operations;

namespace IntoChat.OS.Modules.Apps;

// What a finished file operation means for the conversation that asked for it.
internal static class InoFileWriteReports
{
    internal static string Outcome(InoFileWrite write, OperationResult result) => result switch
    {
        OperationResult.Succeeded => $"Created {write.Name} in {write.Folder}.",
        OperationResult.Failed { Code: "declined" or "expired" } failed => $"Did not create {write.Name}: {failed.Message}",
        OperationResult.Failed failed => $"Could not create {write.Name}: {failed.Message}",
        OperationResult.Cancelled => $"Did not create {write.Name}: the request was cancelled.",
        _ => $"The request to create {write.Name} ended.",
    };

    // The state with the outcome recorded, or null when the operation is not one of ours or was already settled.
    internal static (InoFilesState Next, string Outcome)? Settle(InoFilesState state, OperationDone done)
    {
        var index = Array.FindIndex(state.Writes, item => item.Operation == done.Operation.Value && item.Outcome is null);
        if (index < 0) { return null; }
        var outcome = Outcome(state.Writes[index], done.Result);
        var writes = state.Writes.ToArray();
        writes[index] = writes[index] with { Outcome = outcome };
        return (state with { Writes = writes }, outcome);
    }

    // Outcomes still to post, oldest first.
    internal static InoFileWrite[] Unposted(InoFilesState state)
        => [.. state.Writes.Where(item => item.Outcome is not null && item.Conversation.Length > 0)];

    // The caller's conversation is the agent's full key, "{brain}/{key}"; the neuron key inside the brain drops the brain.
    internal static string AgentKey(string brain, string conversation)
        => conversation.StartsWith(brain + "/", StringComparison.Ordinal) ? conversation[(brain.Length + 1)..] : conversation;
}
