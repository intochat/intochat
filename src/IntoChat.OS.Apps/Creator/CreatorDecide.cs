namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Pure decision functions for CreatorApp following the State + Decide + Render pattern.
/// </summary>
public static class CreatorDecide
{
    public static CreatorState SelectNode(CreatorState state, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var selection = Selection.Parse(nodeId);
        return state with
        {
            Selected = selection,
            Transcript = null,
            ReplayCursor = null
        };
    }

    public static CreatorState SelectRun(CreatorState state, string runId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return state with
        {
            ActiveRunId = runId,
            Status = $"Inspecting change run {runId}"
        };
    }

    public static CreatorState SetDraft(CreatorState state, string draft)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Draft = draft ?? string.Empty };
    }

    public static CreatorState SetChangeDraft(CreatorState state, string changeDraft)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { ChangeDraft = changeDraft ?? string.Empty };
    }

    public static CreatorState SetStatus(CreatorState state, string status)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Status = status ?? string.Empty };
    }

    public static CreatorState SetTranscript(CreatorState state, string? transcript)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Transcript = transcript };
    }

    public static CreatorState SetStage(CreatorState state, string? stage)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { SelectedStage = stage };
    }

    public static CreatorState ClearSelection(CreatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with
        {
            Selected = null,
            ActiveRunId = null,
            Transcript = null,
            ReplayCursor = null
        };
    }
}
