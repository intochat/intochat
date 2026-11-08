namespace IntoChat.OS.Modules.Apps.Flows;

// What a build flow's own detached choice ask calls back on it, so the answer is saved in a turn. Not for other callers: the call
// carries a one-use proof the flow handed its ask, and one without a live proof is ignored.
public interface IBuildChoices
{
    Task ChoiceAnswered(string planId, bool approved, string proof);
}
