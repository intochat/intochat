using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

internal interface IFoundryWhisperRuntime
{
    Task<ILoadedWhisperModel> LoadModelAsync(AIOptions options, ILogger logger, CancellationToken cancellationToken);
}

internal interface ILoadedWhisperModel
{
    string Id { get; }
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken);
    Task UnloadAsync(ILogger logger);
}

// Local Whisper via Microsoft Foundry Local (IAW port). Owned by the AI module.
// Loaded lazily on the first transcription request so host startup never blocks on model downloads.
internal sealed class FoundryLocalTranscriptionService :
    IAudioTranscriptionService,
    IHostedService,
    IAsyncDisposable
{
    public static readonly string DefaultTranscriptionKey = VoiceToTextHosting.DefaultTranscriptionKey;

    private static readonly string[] OggExtensions = [".ogg", ".opus", ".oga"];

    private readonly AIOptions _options;
    private readonly IAudioConverter? _audioConverter;
    private readonly ILogger<FoundryLocalTranscriptionService> _logger;
    private readonly IFoundryWhisperRuntime _runtime;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ILoadedWhisperModel? _loadedModel;
    private bool _disposed;

    public FoundryLocalTranscriptionService(
        IConfiguration configuration,
        ILogger<FoundryLocalTranscriptionService> logger,
        IAudioConverter? audioConverter = null)
        : this(Options.Create(AIOptionsRuntime.Read(configuration)), logger, audioConverter)
    {
    }

    public FoundryLocalTranscriptionService(
        IOptions<AIOptions> options,
        ILogger<FoundryLocalTranscriptionService> logger,
        IAudioConverter? audioConverter = null)
        : this(options, logger, audioConverter, FoundryWhisperRuntime.Instance)
    {
    }

    internal FoundryLocalTranscriptionService(
        IOptions<AIOptions> options,
        ILogger<FoundryLocalTranscriptionService> logger,
        IAudioConverter? audioConverter,
        IFoundryWhisperRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtime);

        _options = options.Value;
        _logger = logger;
        _audioConverter = audioConverter;
        _runtime = runtime;
    }

    public bool IsReady { get; private set; }
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!InitializationFailed && !_disposed);
    public bool InitializationFailed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string ModelId => _loadedModel?.Id
        ?? TranscriptionModel.FindByMarkerName(_options.Default.Transcription ?? string.Empty)?.Id
        ?? "unconfigured";

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
            await UnloadLoadedModelAsync().ConfigureAwait(false);
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

        var model = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

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
            return await model.TranscribeAsync(fileToTranscribe, cancellationToken).ConfigureAwait(false);
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
            await UnloadLoadedModelAsync().ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
            _initLock.Dispose();
        }
    }

    private async Task<ILoadedWhisperModel> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loadedModel is { } ready)
        {
            return ready;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_loadedModel is { } loaded)
            {
                return loaded;
            }

            if (InitializationFailed)
            {
                throw new NotSupportedException($"Whisper is not available: {ErrorMessage}");
            }

            _logger.LogInformation("Initializing Foundry Local for Whisper on first transcription request…");
            var model = await _runtime.LoadModelAsync(_options, _logger, cancellationToken).ConfigureAwait(false);
            _loadedModel = model;
            IsReady = true;
            InitializationFailed = false;
            ErrorMessage = null;
            _logger.LogInformation("Whisper model ready: {ModelId}", model.Id);
            return model;
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

    private async Task UnloadLoadedModelAsync()
    {
        var loaded = _loadedModel;
        _loadedModel = null;
        IsReady = false;
        if (loaded is null)
        {
            return;
        }

        _logger.LogInformation("Unloading Whisper model {ModelId}", loaded.Id);
        await loaded.UnloadAsync(_logger).ConfigureAwait(false);
    }

    private sealed class FoundryWhisperRuntime : IFoundryWhisperRuntime
    {
        public static readonly FoundryWhisperRuntime Instance = new();

        private static readonly TranscriptionModel[] LocalModels =
            [.. TranscriptionModel.All.Where(static model => model.Provider is AiProvider.FoundryLocal)];

        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

        public async Task<ILoadedWhisperModel> LoadModelAsync(AIOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            await InitializeFoundryManagerAsync(logger, cancellationToken).ConfigureAwait(false);

            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            var model = await ResolveModelAsync(options, catalog, logger).ConfigureAwait(false);

            logger.LogInformation("Downloading Whisper model {ModelId}…", model.Id);
            await WithTimeout(model.DownloadAsync(), DownloadTimeout, "Whisper model download", cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Loading Whisper model {ModelId}…", model.Id);
            await WithTimeout(model.LoadAsync(), LoadTimeout, "Whisper model load", cancellationToken)
                .ConfigureAwait(false);

            return new FoundryLoadedWhisperModel(model, logger);
        }

        internal static async Task WithTimeout(Task task, TimeSpan timeout, string operation, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException($"{operation} timed out after {timeout.TotalMinutes:F0} minutes");
            }

            await task.ConfigureAwait(false);
        }

        private static async Task InitializeFoundryManagerAsync(ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                _ = FoundryLocalManager.Instance;
            }
            catch (FoundryLocalException)
            {
                logger.LogInformation("Creating Foundry Local manager…");
                var config = new Configuration { AppName = "intochat-os" };
                await FoundryLocalManager.CreateAsync(config, logger).ConfigureAwait(false);
            }

            var manager = FoundryLocalManager.Instance;
            var executionProviders = manager.DiscoverEps();
            foreach (var provider in executionProviders)
            {
                logger.LogInformation(
                    "Foundry execution provider {Name} registered={Registered}",
                    provider.Name,
                    provider.IsRegistered);
            }

            logger.LogInformation("Downloading and registering Foundry execution providers…");
            await WithTimeout(
                manager.DownloadAndRegisterEpsAsync(cancellationToken),
                DownloadTimeout,
                "Foundry execution provider registration",
                cancellationToken).ConfigureAwait(false);

            foreach (var provider in manager.DiscoverEps())
            {
                logger.LogInformation(
                    "Foundry execution provider {Name} registered={Registered} after download",
                    provider.Name,
                    provider.IsRegistered);
            }
        }

        private static async Task<IModel> ResolveModelAsync(AIOptions options, ICatalog catalog, ILogger logger)
        {
            // The key names a marker; the Foundry catalogue is keyed by wire id.
            var configuredId = TranscriptionModel
                .FindByMarkerName(options.Default.Transcription ?? string.Empty)?.Id;
            if (!string.IsNullOrEmpty(configuredId))
            {
                var configured = await catalog.GetModelAsync(configuredId).ConfigureAwait(false);
                if (configured is not null)
                {
                    return configured;
                }

                logger.LogWarning("Configured whisper model '{ModelId}' not found; falling back", configuredId);
            }

            foreach (var whisperModel in LocalModels)
            {
                var found = await catalog.GetModelAsync(whisperModel.Id).ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }

            throw new InvalidOperationException(
                "No whisper model found in Foundry Local catalog. "
                + $"Expected one of: {string.Join(", ", LocalModels.Select(static m => m.Id))}");
        }
    }

    private sealed class FoundryLoadedWhisperModel(IModel model, ILogger logger) : ILoadedWhisperModel
    {
        private readonly IModel _model = model;
        private readonly ILogger _logger = logger;
        private IModel? _cpuFallbackVariant;
        private bool _cudaFailed;

        public string Id => _cpuFallbackVariant?.Id ?? _model.Id;

        public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
        {
            try
            {
                return await RunTranscriptionAsync(audioFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_cudaFailed && IsCudaError(ex))
            {
                _logger.LogWarning(ex, "CUDA inference failed on {ModelId}, falling back to CPU", _model.Id);
                _cudaFailed = true;
                await FallbackToCpuVariantAsync(cancellationToken).ConfigureAwait(false);
                return await RunTranscriptionAsync(audioFilePath, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task UnloadAsync(ILogger logger)
        {
            try
            {
                if (_cpuFallbackVariant is not null)
                {
                    await _cpuFallbackVariant.UnloadAsync().ConfigureAwait(false);
                }
                else
                {
                    await _model.UnloadAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error unloading Whisper model");
            }
        }

        private async Task<string> RunTranscriptionAsync(string filePath, CancellationToken ct)
        {
            _logger.LogDebug("Transcribing {Path}", filePath);
            var activeModel = _cpuFallbackVariant ?? _model;

            using var session = new AudioSession(activeModel);
            using var request = new global::Microsoft.AI.Foundry.Local.Request();
            request.AddItem(new AudioItem(filePath));
            using var response = await session.ProcessRequestAsync(request, ct).ConfigureAwait(false);

            var transcription = response
                .OfType<SpeechResultItem>()
                .Select(static item => item.Text)
                .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
                ?.Trim()
                ?? string.Empty;

            _logger.LogInformation("Transcription complete: {Length} chars", transcription.Length);
            return transcription;
        }

        private async Task FallbackToCpuVariantAsync(CancellationToken ct)
        {
            var currentId = _model.Id;
            try
            {
                await _model.UnloadAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogSwallowed(exception, "model unload before the cpu variant");
            }

            var cpuVariant = _model.Variants
                .FirstOrDefault(v => v.Id.Contains("generic-cpu", StringComparison.OrdinalIgnoreCase));
            if (cpuVariant is null)
            {
                throw new InvalidOperationException(
                    $"CUDA inference failed and no CPU variant found for {_model.Alias ?? currentId}");
            }

            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
            var cpuModel = await catalog.GetModelVariantAsync(cpuVariant.Id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"CPU variant {cpuVariant.Id} not found in Foundry Local catalog");

            await FoundryWhisperRuntime.WithTimeout(cpuModel.DownloadAsync(), TimeSpan.FromMinutes(5), "CPU model download", ct)
                .ConfigureAwait(false);
            await FoundryWhisperRuntime.WithTimeout(cpuModel.LoadAsync(), TimeSpan.FromMinutes(2), "CPU model load", ct)
                .ConfigureAwait(false);
            _cpuFallbackVariant = cpuModel;
            _logger.LogInformation("CPU fallback model ready: {ModelId}", cpuModel.Id);
        }

        private static bool IsCudaError(Exception ex)
            => ex.ToString().Contains("CUDNN", StringComparison.OrdinalIgnoreCase)
                || ex.ToString().Contains("CUDA", StringComparison.OrdinalIgnoreCase)
                || ex.GetType().Name.Contains("OnnxRuntimeGenAI", StringComparison.OrdinalIgnoreCase);
    }
}
