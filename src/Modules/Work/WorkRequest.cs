using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Work;

[JsonConverter(typeof(JsonStringEnumConverter<WorkKind>))]
public enum WorkKind
{
    Change,
    Build,
    Plan,
    Verify,
    Deploy,
}

/// <summary>
/// Domain request to orchestrate work (e.g. ChangeFlow, BuildFlow, PlanFlow).
/// </summary>
public sealed record WorkRequest(
    string WorkId,
    string Goal,
    string Subject,
    WorkKind Kind = WorkKind.Change,
    string RequestedBy = "system",
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkId))
        {
            throw new ArgumentException("WorkId must not be empty.", nameof(WorkId));
        }
        if (string.IsNullOrWhiteSpace(Goal))
        {
            throw new ArgumentException("Goal must not be empty.", nameof(Goal));
        }
    }
}
