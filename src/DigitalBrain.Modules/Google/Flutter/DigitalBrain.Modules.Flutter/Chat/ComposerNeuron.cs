using DigitalBrain.Flutter.VoiceInput;
using DigitalBrain.Framework;

namespace DigitalBrain.Flutter.Chat;

[GenerateSerializer, Alias("ui.composer-storage")]
internal sealed record ComposerStorage
{
    [Id(0)] public ComposerState State { get; init; } = new();
    [Id(1)] public ComposerAudio? Audio { get; init; }
}

[GrainType(UIVocabulary.ComposerType)]
internal sealed class ComposerNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ComposerStorage> store)
    : Neuron, IComposer
{
    private ComposerState State => store.State.State;

    public async Task Configure(string context, long generation, string label = "Message")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (context.Length > 512 || label.Length > 128 || generation < 0) { throw new ArgumentException("Invalid composer configuration."); }
        if (generation < State.Generation) { return; }
        if (generation == State.Generation && context != State.Context) { throw new InvalidOperationException("A new context requires a new generation."); }
        if (generation == State.Generation && label == State.Label) { return; }
        var changed = context != State.Context || generation != State.Generation;
        await Save(State with
        {
            Context = context, Generation = generation, Label = label,
            Draft = changed ? "" : State.Draft,
            DraftVersion = changed ? State.DraftVersion + 1 : State.DraftVersion,
            Voice = changed ? null : State.Voice,
            Error = changed ? null : State.Error,
        }, changed ? null : store.State.Audio);
    }

    public Task Edit(string context, string value)
    {
        RequireContext(context);
        ValidateText(value);
        if (value == State.Draft) { return Task.CompletedTask; }
        return Save(State with { Draft = value, DraftVersion = State.DraftVersion + 1, Error = null }, store.State.Audio);
    }

    public async Task<Guid?> Submit(Guid requestId, string context, string value)
    {
        ValidateId(requestId);
        ValidateText(value);
        var text = value.Trim();
        if (State.Submissions.FirstOrDefault(request => request.Id == requestId) is { } prior)
        {
            if (prior.Context != context || prior.Text != text) { throw new InvalidOperationException("This request ID belongs to another submission."); }
            if (prior.Status == "pending") { await PublishAsync(new ComposerSubmitted(this.GetPrimaryKeyString(), prior.Id)); }
            return prior.Id;
        }
        RequireContext(context);
        if (value != State.Draft) { throw new InvalidOperationException("The draft changed. Review it and send again."); }
        if (text.Length == 0) { return null; }
        if (State.Submissions.FirstOrDefault(request => request.Context == context && request.DraftVersion == State.DraftVersion && request.Status == "pending") is { } pending)
        {
            await PublishAsync(new ComposerSubmitted(this.GetPrimaryKeyString(), pending.Id));
            return pending.Id;
        }
        if (State.Submissions.Count(request => request.Status == "pending") >= 16) { throw new InvalidOperationException("Too many messages are waiting. Try again when a submission finishes."); }
        var request = new ComposerSubmission(requestId, context, text, State.DraftVersion);
        // Keep all unacknowledged input even if an older request is slower than later ones.
        var retained = State.Submissions.Where(item => item.Status == "pending")
            .Concat(State.Submissions.Where(item => item.Status != "pending").TakeLast(64)).ToArray();
        await Save(State with { Submissions = [.. retained, request], Error = null }, store.State.Audio,
            new ComposerSubmitted(this.GetPrimaryKeyString(), requestId));
        return requestId;
    }

    public async Task Settle(Guid requestId, string? error = null)
    {
        var request = State.Submissions.FirstOrDefault(item => item.Id == requestId);
        if (request is null || request.Status != "pending") { return; }
        var sameDraft = request.Context == State.Context && request.DraftVersion == State.DraftVersion;
        var accepted = error is null;
        await Save(State with
        {
            Submissions = [.. State.Submissions.Select(item => item.Id == requestId ? item with { Status = accepted ? "accepted" : "refused", Error = error } : item)],
            Draft = accepted && sameDraft ? "" : State.Draft,
            DraftVersion = accepted && sameDraft ? State.DraftVersion + 1 : State.DraftVersion,
            Error = request.Context == State.Context ? error : State.Error,
            Voice = accepted && sameDraft ? null : State.Voice,
        }, accepted && sameDraft ? null : store.State.Audio);
    }

    public Task Defer(Guid requestId, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var request = State.Submissions.FirstOrDefault(item => item.Id == requestId);
        if (request is null || request.Status != "pending") { return Task.CompletedTask; }
        return Save(State with
        {
            Submissions = [.. State.Submissions.Select(item => item.Id == requestId ? item with { Error = error } : item)],
            Error = request.Context == State.Context ? error : State.Error,
        }, store.State.Audio);
    }

    public Task BeginVoice(Guid captureId, string context)
    {
        ValidateId(captureId);
        RequireContext(context);
        if (State.Voice?.Id == captureId) { return Task.CompletedTask; }
        return Save(State with { Voice = new(captureId, context, State.DraftVersion, "recording"), Error = null }, null);
    }

    public Task CaptureVoice(Guid captureId, byte[] audio, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (audio.Length is 0 or > IVoiceInput.MaxAudioBytes) { throw new ArgumentException("Audio must contain between 1 byte and 8 MiB."); }
        if (!mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Only audio can be captured."); }
        if (State.Voice is not { } voice || voice.Id != captureId) { return Task.CompletedTask; }
        if (voice.Status == "transcribing") { return PublishAsync(new ComposerVoiceCaptured(this.GetPrimaryKeyString(), captureId)); }
        if (voice.Status != "recording") { return Task.CompletedTask; }
        return Save(State with { Voice = voice with { Status = "transcribing" } }, new([.. audio], mimeType.Trim().ToLowerInvariant()),
            new ComposerVoiceCaptured(this.GetPrimaryKeyString(), captureId));
    }

    public Task CancelVoice(Guid captureId)
    {
        if (State.Voice is not { } voice || voice.Id != captureId || voice.Status is not ("recording" or "transcribing")) { return Task.CompletedTask; }
        return Save(State with { Voice = voice with { Status = "cancelled" } }, null);
    }

    public Task CompleteVoice(Guid captureId, string? text, string? error = null)
    {
        if (State.Voice is not { } voice || voice.Id != captureId || voice.Status is not ("recording" or "transcribing")) { return Task.CompletedTask; }
        if (error is null && voice.Status != "transcribing") { throw new InvalidOperationException("Audio has not been captured."); }
        if (error is null && string.IsNullOrWhiteSpace(text)) { error = "No speech was detected. Try recording again."; }
        if (error is null && text!.Length > 32000) { error = "The transcript is too long. Try a shorter recording."; }
        var applies = error is null && voice.Context == State.Context && voice.DraftVersion == State.DraftVersion;
        return Save(State with
        {
            Draft = applies ? text!.Trim() : State.Draft,
            DraftVersion = applies ? State.DraftVersion + 1 : State.DraftVersion,
            Voice = voice with
            {
                Status = error is not null ? "failed" : applies ? "ready" : "discarded",
                Error = error ?? (applies ? null : "Your draft changed; the transcription was not applied. Record again when ready."),
            },
        }, null);
    }

    public Task<ComposerAudio?> ReadAudio(Guid captureId) => Task.FromResult(State.Voice is { Status: "transcribing" } voice && voice.Id == captureId ? store.State.Audio : null);
    public Task<ComposerState> Read() => Task.FromResult(State);

    private void RequireContext(string context)
    {
        if (string.IsNullOrWhiteSpace(State.Context) || context != State.Context) { throw new InvalidOperationException("The conversation changed. Review the current draft before continuing."); }
    }

    private static void ValidateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 32000) { throw new ArgumentException("A draft has at most 32000 characters."); }
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) { throw new ArgumentException("An operation needs a unique ID."); }
    }

    private async Task Save(ComposerState state, ComposerAudio? audio, Signal? signal = null)
    {
        var previous = store.State;
        var next = state with { Name = this.GetPrimaryKeyString(), Revision = State.Revision + 1 };
        store.State = new() { State = next, Audio = audio };
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new ComposerChanged(next.Name, next.Revision));
        if (signal is not null) { await PublishAsync(signal); }
    }
}
