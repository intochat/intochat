namespace DigitalBrain.UI.Controls;

/// <summary>The state of a chat thread neuron: its messages, oldest first (the last 500 are kept).</summary>
public sealed record ChatThreadState
{
    /// <summary>The most messages a thread holds; older ones are dropped.</summary>
    public const int MaxMessages = 500;

    /// <summary>The held messages, oldest first.</summary>
    [Showcase("""{"kind":"chatThreadView","key":"conversation","messages":[{"id":"m1","role":"user","text":"Summarise my week","status":"completed"},{"id":"m2","role":"assistant","text":"","status":"failed","retry":{"kind":"button","key":"retry:m2","label":"Try again","command":{"target":".","signal":"control.retry","args":{"id":"m2"}},"automationName":"Try again"},"error":{"kind":"errorLine","what":"The model did not answer."}}],"composer":{"key":"composer","onSubmit":{"owner":"control.chatThread","signal":"control.submitText"}},"props":{"placeholder":"Ask anything.","canSend":true,"dictation":false,"status":"failed","working":false,"isEnabled":true,"automationName":"Conversation"}}""")]
    public IReadOnlyList<ThreadMessage> Messages { get; init; } = [];

    /// <summary>Whether the props the parent routed with its last write say it is working: the stop is drawn.</summary>
    public bool ShowsStop { get; init; }

    /// <summary>Whether the props the parent routed with its last write carry an error that retries: the error line's retry is drawn.</summary>
    public bool ShowsRetry { get; init; }

    /// <summary>
    /// False while the props the parent routed with its last write say it cannot take a message (it is busy or paused): the thread then
    /// refuses the composer's text, so a send while a reply runs starts nothing and the person keeps the draft.
    /// </summary>
    public bool AcceptsText { get; init; } = true;

    /// <inheritdoc />
    public bool Equals(ChatThreadState? other) => other is not null && ShowsStop == other.ShowsStop && ShowsRetry == other.ShowsRetry && AcceptsText == other.AcceptsText && Messages.SequenceEqual(other.Messages);

    /// <inheritdoc />
    public override int GetHashCode() => Messages.Count;
}

/// <summary>
/// What a chat thread neuron draws: its messages (a failed one with its reason and a retry), and the composer, stop and retry nodes the
/// client activates on it. The client draws the placeholder, model, status, empty text and error from the slot's props.
/// </summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Messages">The messages drawn, oldest first.</param>
/// <param name="Composer">The composer: it submits the typed text to the thread.</param>
/// <param name="Stop">The stop button: present only while the props the parent last routed say it is working.</param>
/// <param name="Retry">The error line's retry: present only while the props the parent last routed carry an error that retries.</param>
/// <param name="Props">The slot's props, set only where a reader outside the client draws the slot in place (a screen directory); the
/// neuron never sets it and the client reads the slot's own props.</param>
[ViewNode("chatThreadView")]
public sealed record ChatThreadView(string Key, IReadOnlyList<MessageNode> Messages, ComposerNode Composer, Button? Stop = null, Button? Retry = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ChatThreadProps? Props = null) : Node
{
    /// <inheritdoc />
    public bool Equals(ChatThreadView? other) => other is not null && Key == other.Key && Messages.SequenceEqual(other.Messages)
        && Equals(Composer, other.Composer) && Equals(Stop, other.Stop) && Equals(Retry, other.Retry) && Equals(Props, other.Props);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Messages.Count);
}
