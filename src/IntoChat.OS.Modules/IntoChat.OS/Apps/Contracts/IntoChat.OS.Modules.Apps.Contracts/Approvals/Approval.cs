namespace IntoChat.OS.Modules.Apps.Approvals;

// What an inbox entry is about. FlowStep: a flow waits on the plan. SynapseStaged: a staged synapse version waits for its needs to be
// approved. StorageWrite: a destructive storage action. Grant: a permission someone asks for.
public enum ApprovalKind
{
    FlowStep,
    SynapseStaged,
    StorageWrite,
    Grant,
}

// One open question of the brain's confirmation inbox, enriched with what raised it. Subject is the flow id, the synapse source key or
// the storage target's display name; TraceId is the flow's, empty for the other kinds.
public sealed record Approval(
    string PlanId,
    ApprovalKind Kind,
    string Subject,
    string Title,
    string Detail,
    DateTimeOffset Requested,
    DateTimeOffset Expires,
    string TraceId);
