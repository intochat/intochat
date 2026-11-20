using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;
/// <summary>One pre-filled change row inside an <see cref="ApprovalCardNode"/>.</summary>
public sealed record ApprovalRowNode(string Label, string OldValue, string NewValue, string? Amount = null, bool Warn = false);

/// <summary>Pre-filled inline approval card.</summary>
[ViewNode("approvalCard")]
public sealed record ApprovalCardNode(
    string Key,
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<ApprovalRowNode> Rows,
    string ValueLabel = "Value",
    string SystemName = "DigitalBrain",
    string Status = "pending",
    Resolve? Resolve = null,
    string? SourceNeuronUri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Icon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CapabilityLabel = null,
    string AutomationName = "") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Title"/> / <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName : !string.IsNullOrWhiteSpace(Title) ? Title : Key ?? "";

    /// <inheritdoc />
    public bool Equals(ApprovalCardNode? other) =>
        other is not null &&
        Id == other.Id &&
        Title == other.Title &&
        Summary == other.Summary &&
        ValueLabel == other.ValueLabel &&
        SystemName == other.SystemName &&
        Status == other.Status &&
        SourceNeuronUri == other.SourceNeuronUri &&
        Icon == other.Icon &&
        CapabilityLabel == other.CapabilityLabel &&
        Key == other.Key &&
        AutomationName == other.AutomationName &&
        Equals(Resolve, other.Resolve) &&
        Rows.SequenceEqual(other.Rows);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Title, Status, Rows.Count);
}

/// <summary>One numbered option inside a <see cref="ChoiceCardNode"/>.</summary>
public sealed record ChoiceOptionNode(string Title, string? Detail = null, Button? Pick = null, string? Icon = null);

/// <summary>Disambiguation choice card (2 to 5 options).</summary>
[ViewNode("choiceCard")]
public sealed record ChoiceCardNode(
    string Id,
    string Question,
    IReadOnlyList<ChoiceOptionNode> Options,
    int? PickedIndex = null) : Node
{
    /// <inheritdoc />
    public bool Equals(ChoiceCardNode? other) =>
        other is not null &&
        Id == other.Id &&
        Question == other.Question &&
        PickedIndex == other.PickedIndex &&
        Options.SequenceEqual(other.Options);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Question, Options.Count);
}

/// <summary>Pre-filled outbound message draft card (email, Slack, SMS).</summary>
[ViewNode("draftCard")]
public sealed record DraftCardNode(
    string Id,
    string Channel,
    string To,
    string Body,
    string From,
    string? Subject = null,
    string Status = "pending",
    Button? Send = null,
    Button? Discard = null) : Node;

/// <summary>A key-value field in a <see cref="RecordCardNode"/>.</summary>
public sealed record RecordFieldNode(string Label, string Value, bool Warn = false);

/// <summary>Structured record summary card.</summary>
[ViewNode("recordCard")]
public sealed record RecordCardNode(
    string ObjectLabel,
    string Name,
    IReadOnlyList<RecordFieldNode> Fields,
    string? Subtitle = null,
    string LinkLabel = "Open Neuron",
    Button? Open = null) : Node
{
    /// <inheritdoc />
    public bool Equals(RecordCardNode? other) =>
        other is not null &&
        ObjectLabel == other.ObjectLabel &&
        Name == other.Name &&
        Subtitle == other.Subtitle &&
        LinkLabel == other.LinkLabel &&
        Equals(Open, other.Open) &&
        Fields.SequenceEqual(other.Fields);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ObjectLabel, Name, Fields.Count);
}

/// <summary>Secure credential request card.</summary>
[ViewNode("secretCard")]
public sealed record SecretCardNode(
    string Name,
    string Why,
    string SavedIn = "Variables and secrets",
    string Status = "pending",
    Button? Enter = null,
    Button? Skip = null) : Node;

/// <summary>A step in a <see cref="JobCardNode"/>.</summary>
public sealed record JobStepNode(string Label, string Status = "waiting", string? Detail = null);

/// <summary>Multi-step background job progress card.</summary>
[ViewNode("jobCard")]
public sealed record JobCardNode(
    string Title,
    IReadOnlyList<JobStepNode> Steps,
    double Progress = 0,
    string Status = "running",
    string? ResultText = null,
    Button? Cancel = null) : Node
{
    /// <inheritdoc />
    public bool Equals(JobCardNode? other) =>
        other is not null &&
        Title == other.Title &&
        Progress.Equals(other.Progress) &&
        Status == other.Status &&
        ResultText == other.ResultText &&
        Equals(Cancel, other.Cancel) &&
        Steps.SequenceEqual(other.Steps);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Title, Status, Steps.Count);
}

[ViewNode("diffCard")]
public sealed record DiffCardNode(
    string Id,
    string Repo,
    string TargetBranch,
    string HeadSha,
    int FilesChanged,
    int Additions,
    int Deletions,
    IReadOnlyList<DiffFileNode> Files) : Node
{
    /// <inheritdoc />
    public bool Equals(DiffCardNode? other) =>
        other is not null
        && Id == other.Id
        && Repo == other.Repo
        && TargetBranch == other.TargetBranch
        && HeadSha == other.HeadSha
        && FilesChanged == other.FilesChanged
        && Additions == other.Additions
        && Deletions == other.Deletions
        && Files.SequenceEqual(other.Files);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Repo, TargetBranch, HeadSha, Files.Count);
}

/// <summary>One changed file inside a <see cref="DiffCardNode"/>.</summary>
public sealed record DiffFileNode(
    string Path,
    string ChangeKind,
    int Additions,
    int Deletions,
    IReadOnlyList<DiffHunkNode> Hunks)
{
    /// <inheritdoc />
    public bool Equals(DiffFileNode? other) =>
        other is not null
        && Path == other.Path
        && ChangeKind == other.ChangeKind
        && Additions == other.Additions
        && Deletions == other.Deletions
        && Hunks.SequenceEqual(other.Hunks);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Path, ChangeKind, Additions, Deletions, Hunks.Count);
}

/// <summary>One unified diff hunk inside a <see cref="DiffFileNode"/>.</summary>
public sealed record DiffHunkNode(
    string Header,
    IReadOnlyList<string> Lines)
{
    /// <inheritdoc />
    public bool Equals(DiffHunkNode? other) =>
        other is not null
        && Header == other.Header
        && Lines.SequenceEqual(other.Lines);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Header, Lines.Count);
}
