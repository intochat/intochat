namespace IntoChat.OS.Modules.AI;

// Stands in whenever no transcription model is usable, so a voice
// misconfiguration answers 503 with a reason instead of refusing to boot the
// silo. Every other surface keeps working.
internal sealed class UnavailableTranscriptionService : IAudioTranscriptionService
{
    private const string NotConfigured =
        "Voice-to-text is not configured. Configure Role.Hear with AIOptions.WithRole(Role.Hear, ...).";

    public UnavailableTranscriptionService()
        : this(NotConfigured)
    {
    }

    public UnavailableTranscriptionService(string errorMessage)
        => ErrorMessage = errorMessage;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public bool InitializationFailed => true;
    public string? ErrorMessage { get; }
    public string ModelId => "none";

    public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException(ErrorMessage));

    public Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        CancellationToken cancellationToken = default)
        => Task.FromException<string>(new InvalidOperationException(ErrorMessage));
}
