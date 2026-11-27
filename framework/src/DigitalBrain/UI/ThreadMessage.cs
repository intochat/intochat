using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>One message of a chat thread: data only (its cards are data nodes).</summary>
/// <param name="Id">The message id; a thread holds each id once.</param>
/// <param name="Role">Who wrote it: user, assistant or system.</param>
/// <param name="Text">The message text.</param>
/// <param name="Status">completed, sending, running, failed, interrupted or cancelled.</param>
public sealed record ThreadMessage(string Id, string Role, string Text, string Status = "completed")
{
    /// <summary>The model that wrote it, as the user reads it; null when unknown.</summary>
    public string? Model { get; init; }

    /// <summary>The brand icon key of <see cref="Model"/>; null when unknown.</summary>
    public string? ProviderIcon { get; init; }

    /// <summary>Why a failed, interrupted or cancelled message has no answer, as the user reads it; null or blank draws "The model did not answer."</summary>
    public string? Failure { get; init; }

    /// <summary>The tool steps taken while writing it.</summary>
    public IReadOnlyList<ActivityNode> Activities { get; init; } = [];

    /// <summary>The cards shown with it (data nodes).</summary>
    public IReadOnlyList<Node> Cards { get; init; } = [];

    /// <summary>The structured attachments and playable voice-note clips attached to this message.</summary>
    public IReadOnlyList<AttachmentNode> Attachments { get; init; } = [];

    /// <summary>True on the latest completed assistant reply when no turn is active: draws a Regenerate action.</summary>
    public bool CanRegenerate { get; init; }

    /// <summary>Authenticated speaker attribution for this message, when present.</summary>
    public SenderIdentity? Sender { get; init; }

    /// <summary>Whether the message failed, was interrupted or was cancelled: it draws a reason and a retry.</summary>
    [JsonIgnore]
    public bool IsFailed => Status is "failed" or "interrupted" or "cancelled";

    /// <inheritdoc />
    public bool Equals(ThreadMessage? other) => other is not null && Id == other.Id && Role == other.Role && Text == other.Text && Status == other.Status
        && Model == other.Model && ProviderIcon == other.ProviderIcon && Failure == other.Failure && CanRegenerate == other.CanRegenerate
        && Equals(Sender, other.Sender)
        && Activities.SequenceEqual(other.Activities) && Cards.SequenceEqual(other.Cards) && Attachments.SequenceEqual(other.Attachments);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Role, Text, Status, Failure, CanRegenerate);
}

/// <summary>A change to a held message: each member set replaces that member; null leaves it.</summary>
/// <param name="Text">The new text.</param>
/// <param name="Status">The new status.</param>
/// <param name="Model">The new model.</param>
/// <param name="Failure">The new failure reason.</param>
/// <param name="Activities">The new tool steps.</param>
/// <param name="Cards">The new cards.</param>
public sealed record ThreadMessagePatch(string? Text = null, string? Status = null, string? Model = null, string? Failure = null,
    IReadOnlyList<ActivityNode>? Activities = null, IReadOnlyList<Node>? Cards = null)
{
    /// <summary><paramref name="message"/> with this patch applied.</summary>
    public ThreadMessage ApplyTo(ThreadMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message with
        {
            Text = Text ?? message.Text,
            Status = Status ?? message.Status,
            Model = Model ?? message.Model,
            Failure = Failure ?? message.Failure,
            Activities = Activities ?? message.Activities,
            Cards = Cards ?? message.Cards,
        };
    }

    /// <inheritdoc />
    public bool Equals(ThreadMessagePatch? other) => other is not null && Text == other.Text && Status == other.Status && Model == other.Model && Failure == other.Failure
        && Same(Activities, other.Activities) && Same(Cards, other.Cards);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Text, Status, Model, Failure);

    private static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
}

/// <summary>Why a chat thread's parent shows an error that no message carries (a send that never started, a budget).</summary>
/// <param name="What">What went wrong.</param>
/// <param name="Detail">More about it, or null.</param>
/// <param name="ActionLabel">The label of its action, or null for none.</param>
/// <param name="Open">The window its action opens (client-local), or null.</param>
/// <param name="Retry">True: its action asks the parent to try the last turn again (RetryRequested with an empty id).</param>
public sealed record ChatThreadError(string What, string? Detail = null, string? ActionLabel = null, string? Open = null, bool Retry = false);
