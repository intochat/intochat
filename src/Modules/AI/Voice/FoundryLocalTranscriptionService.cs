using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

// Local Whisper via the Devices module's Foundry Local runtime (ILocalRuntime "foundry-local" -> Hear).
// Acquired lazily on the first transcription request so host startup never blocks on model downloads.
internal sealed class FoundryLocalTranscriptionService :
    IAudioTranscriptionService,
    IHostedService,
    IAsyncDisposable
{
    public static readonly string RolesKey = VoiceToTextHosting.RolesKey;

    private static readonly string[] OggExtensions = [".ogg", ".opus", ".oga"];

    private readonly AIOptions _options;
    private readonly IAudioConverter? _audioConverter;
    private readonly ILogger<FoundryLocalTranscriptionService> _logger;
    private readonly IReadOnlyList<ILocalRuntime> _runtimes;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ISpeechToTextClient? _client;
    private string? _loadedModelId;
    private bool _disposed;

    public FoundryLocalTranscriptionService(
        IConfiguration configuration,
        ILogger<FoundryLocalTranscriptionService> logger,
        IEnumerable<ILocalRuntime> runtimes,
        IAudioConverter? audioConverter = null)
        : this(Options.Create(AIOptionsRuntime.Read(configuration)), logger, runtimes, audioConverter)
    {
    }

    public FoundryLocalTranscriptionService(
        IOptions<AIOptions> options,
        ILogger<FoundryLocalTranscriptionService> logger,
        IEnumerable<ILocalRuntime> runtimes,
        IAudioConverter? audioConverter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtimes);

        _options = options.Value;
        _logger = logger;
        _runtimes = [.. runtimes];
        _audioConverter = audioConverter;
    }

    public bool IsReady { get; private set; }
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!InitializationFailed && !_disposed);
    public bool InitializationFailed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string ModelId => _loadedModelId
        ?? ConfiguredHearModelId(_options)
        ?? "unconfigured";

    private static string? ConfiguredHearModelId(AIOptions options)
    {
        var id = RoleMaps.Explicit(options, topology: null, Role.Hear);
        return id is { Value.Length: > 0 } value
            ? KnownModels.Find(value.Value)?.Id.Value ?? value.Value
            : null;
    }

    // Intentionally a no-op: Whisper is downloaded and loaded on the first transcription request
    // rather than stalling host startup for minutes.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseClientAsync().ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (client, modelId) = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        var extension = Path.GetExtension(audioFilePath).ToLowerInvariant();
        var fileToTranscribe = audioFilePath;
        string? convertedFilePath = null;

        if (OggExtensions.Contains(extension) && _audioConverter is not null)
        {
            _logger.LogDebug("Converting {Source} to WAV", audioFilePath);
            convertedFilePath = _audioConverter.ConvertToWav(audioFilePath);
            fileToTranscribe = convertedFilePath;
        }

        try
        {
            await using var stream = File.OpenRead(fileToTranscribe);
            var speechOptions = new SpeechToTextOptions
            {
                ModelId = modelId,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["filePath"] = fileToTranscribe },
            };
            var response = await client.GetTextAsync(stream, speechOptions, cancellationToken).ConfigureAwait(false);
            return response.Text?.Trim() ?? string.Empty;
        }
        finally
        {
            if (convertedFilePath is not null && File.Exists(convertedFilePath))
            {
                File.Delete(convertedFilePath);
            }
        }
    }

    public async Task<string> TranscribeAsync(
        Stream audioStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".wav";
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"db_voice_{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var fileStream = File.Create(tempPath))
            {
                await audioStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            return await TranscribeAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await ReleaseClientAsync().ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
            _initLock.Dispose();
        }
    }

    private async Task<(ISpeechToTextClient Client, string ModelId)> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } ready && _loadedModelId is { } readyModel)
        {
            return (ready, readyModel);
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_client is { } loaded && _loadedModelId is { } loadedModel)
            {
                return (loaded, loadedModel);
            }

            if (InitializationFailed)
            {
                throw new NotSupportedException($"Whisper is not available: {ErrorMessage}");
            }

            var modelId = ConfiguredHearModelId(_options) ?? KnownModels.Defaults.Device[Role.Hear].Value;
            var foundry = _runtimes.FirstOrDefault(static r => r.Id == RuntimeId.FoundryLocal)
                ?? throw new InvalidOperationException("Foundry Local runtime is not registered on this device.");

            var status = await foundry.Probe(cancellationToken).ConfigureAwait(false);
            if (!status.Reachable)
            {
                throw new InvalidOperationException("Foundry Local runtime is not reachable.");
            }

            var client = foundry.Hear(new ModelId(modelId))
                ?? throw new InvalidOperationException($"Foundry Local runtime does not provide speech-to-text for '{modelId}'.");

            _client = client;
            _loadedModelId = modelId;
            IsReady = true;
            InitializationFailed = false;
            ErrorMessage = null;
            _logger.LogInformation("Whisper model ready through Foundry Local runtime: {ModelId}", modelId);
            return (client, modelId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            InitializationFailed = true;
            ErrorMessage = ex.Message;
            IsReady = false;
            _logger.LogError(ex, "Whisper initialization failed");
            throw new NotSupportedException($"Whisper is not available: {ex.Message}", ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task ReleaseClientAsync()
    {
        var client = _client;
        var modelId = _loadedModelId;
        _client = null;
        _loadedModelId = null;
        IsReady = false;
        if (client is null)
        {
            return;
        }

        _logger.LogInformation("Releasing Whisper client for {ModelId}", modelId);
        if (client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            client.Dispose();
        }
    }
}
