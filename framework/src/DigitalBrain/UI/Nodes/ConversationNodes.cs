using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

[ViewNode("conversation")]
public sealed record Conversation(
    string Key,
    IReadOnlyList<MessageNode> Messages,
    ComposerNode Composer,
    Node? Waiting = null,
    string Status = "ready",
    ErrorLineNode? Error = null,
    Button? Stop = null,
    EmptyStateNode? Empty = null,
    string AutomationName = "") : Node
{
    /// <summary>The accessible name: the given one, or <see cref="Key"/> when none was given.</summary>
    public string AutomationName { get; init; } = !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName : Key ?? "";

    /// <inheritdoc />
    public bool Equals(Conversation? other) =>
        other is not null
        && Key == other.Key
        && AutomationName == other.AutomationName
        && Messages.SequenceEqual(other.Messages)
        && Equals(Composer, other.Composer)
        && Equals(Waiting, other.Waiting)
        && Status == other.Status
        && Equals(Error, other.Error)
        && Equals(Stop, other.Stop)
        && Equals(Empty, other.Empty);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Messages.Count, Status);
}

/// <summary>One message in a <see cref="Conversation"/>.</summary>
public sealed record MessageNode(
    string Id,
    string Role,
    string Text,
    IReadOnlyList<Node>? Cards = null,
    IReadOnlyList<ActivityNode>? Activities = null,
    string Status = "completed",
    string? Model = null,
    Button? Retry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderIcon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorLineNode? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SenderIdentity? Sender = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AttachmentNode>? Attachments = null)
{
    /// <inheritdoc />
    public bool Equals(MessageNode? other) =>
        other is not null
        && Id == other.Id
        && Role == other.Role
        && Text == other.Text
        && SequenceEquals(Cards, other.Cards)
        && SequenceEquals(Activities, other.Activities)
        && Status == other.Status
        && Model == other.Model
        && ModelLabel == other.ModelLabel
        && ProviderIcon == other.ProviderIcon
        && Equals(Retry, other.Retry)
        && Equals(Error, other.Error)
        && Equals(Sender, other.Sender)
        && SequenceEquals(Attachments, other.Attachments);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Role, Text, Status);

    private static bool SequenceEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
}

/// <summary>One structured attachment or playable voice-note clip carried by a <see cref="MessageNode"/>.</summary>
public sealed record AttachmentNode(
    string Id,
    string Kind,
    string FileName,
    string MediaType,
    int SizeBytes,
    string Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Transcript = null);

/// <summary>One tool / activity step inside a <see cref="MessageNode"/>.</summary>
public sealed record ActivityNode(
    string Id,
    string Title,
    string Status = "done",
    string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Icon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null);

/// <summary>The message input composer at the bottom of a <see cref="Conversation"/>.</summary>
/// <remarks>A send carries <c>OnSubmit</c> with the typed text as <c>value</c>.</remarks>
public sealed record ComposerNode(
    string Key,
    Command OnSubmit,
    string Placeholder = "Message",
    bool Enabled = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveModelLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveProviderIcon = null,
    bool Dictation = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveContextLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Command? OnClearContext = null);

/// <summary>An addressable model-picker activation target drawn by a chat thread.</summary>
/// <param name="Key">The node's activation key.</param>
/// <param name="OnSubmit">The command a pick sends, with the chosen model id as <c>value</c>.</param>
public sealed record ModelSelectNode(string Key, Command OnSubmit);

