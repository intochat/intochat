namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Pure decision facade delegating directly to the rich DDD methods on <see cref="CreatorState"/>.
/// </summary>
public static class CreatorDecide
{
    public static CreatorState SelectNode(CreatorState state, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SelectNode(nodeId);
    }

    public static CreatorState SelectRun(CreatorState state, string runId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SelectRun(runId);
    }

    public static CreatorState SetDraft(CreatorState state, string draft)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SetDraft(draft);
    }

    public static CreatorState SetChangeDraft(CreatorState state, string changeDraft)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SetChangeDraft(changeDraft);
    }

    public static CreatorState SetStatus(CreatorState state, string status)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SetStatus(status);
    }

    public static CreatorState SetTranscript(CreatorState state, string? transcript)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SetTranscript(transcript);
    }

    public static CreatorState SetStage(CreatorState state, string? stage)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SetStage(stage);
    }

    public static CreatorState ClearSelection(CreatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ClearSelection();
    }
}
