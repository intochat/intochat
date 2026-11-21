using System.Collections.Concurrent;
using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Runtimes.FoundryLocal;

internal sealed record FoundryCatalogModel(
    string Id,
    string? Alias,
    string? Task,
    long? FileSizeBytes,
    bool SupportsToolCalling,
    bool HasNpuVariant,
    string? ExecutionProvider);

internal interface IFoundryLocalApi
{
    Task CreateManagerAsync(string? cacheDirectory, CancellationToken ct);
    Task<IReadOnlyList<FoundryCatalogModel>> ListCatalogAsync(CancellationToken ct);
    Task<IReadOnlyList<FoundryCatalogModel>> ListCachedAsync(CancellationToken ct);
    Task<IReadOnlyList<ModelId>> ListLoadedAsync(CancellationToken ct);
    Task DownloadAsync(string modelId, Action<float> onProgress, CancellationToken ct);
    Task LoadAsync(string modelId, CancellationToken ct);
    Task RemoveAsync(string modelId, CancellationToken ct);
    Task<Uri> StartWebServiceAsync(CancellationToken ct);
    Task<string> TranscribeFileAsync(string modelId, string audioFilePath, CancellationToken ct);
    Task UnloadAsync(string modelId, CancellationToken ct);
}

// Owns the single FoundryLocalManager lifecycle and web service startup per process.
internal sealed class FoundryLocalHost(IFoundryLocalApi api, IOptionsMonitor<DevicesOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _webGate = new(1, 1);
    private bool _managerCreated;
    private Uri? _webServiceEndpoint;

    internal Uri? WebServiceEndpoint => Volatile.Read(ref _webServiceEndpoint);

    internal async Task Manager(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _managerCreated))
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_managerCreated)
            {
                return;
            }

            var cacheDir = options.CurrentValue.FoundryLocal.CacheDirectory;
            await api.CreateManagerAsync(string.IsNullOrWhiteSpace(cacheDir) ? null : cacheDir.Trim(), ct).ConfigureAwait(false);
            Volatile.Write(ref _managerCreated, true);
        }
        finally
        {
            _initGate.Release();
        }
    }

    internal async Task<IReadOnlyList<FoundryCatalogModel>> ListCatalogAsync(CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        return await api.ListCatalogAsync(ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<FoundryCatalogModel>> ListCachedAsync(CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        return await api.ListCachedAsync(ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ModelId>> ListLoadedAsync(CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        return await api.ListLoadedAsync(ct).ConfigureAwait(false);
    }

    internal async Task DownloadAsync(string modelId, Action<float> onProgress, CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        await api.DownloadAsync(modelId, onProgress, ct).ConfigureAwait(false);
    }

    internal async Task LoadAsync(string modelId, CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        await api.LoadAsync(modelId, ct).ConfigureAwait(false);
    }

    internal async Task RemoveAsync(string modelId, CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        await api.RemoveAsync(modelId, ct).ConfigureAwait(false);
    }

    internal async Task<Uri> EnsureWebServiceAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _webServiceEndpoint) is { } ready)
        {
            return ready;
        }

        await Manager(ct).ConfigureAwait(false);
        await _webGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_webServiceEndpoint is { } existing)
            {
                return existing;
            }

            var uri = await api.StartWebServiceAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _webServiceEndpoint, uri);
            return uri;
        }
        finally
        {
            _webGate.Release();
        }
    }

    internal async Task<string> TranscribeFileAsync(string modelId, string audioFilePath, CancellationToken ct)
    {
        await Manager(ct).ConfigureAwait(false);
        return await api.TranscribeFileAsync(modelId, audioFilePath, ct).ConfigureAwait(false);
    }

    internal async Task UnloadAsync(string modelId, CancellationToken ct)
    {
        if (!Volatile.Read(ref _managerCreated))
        {
            return;
        }
        await api.UnloadAsync(modelId, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _initGate.Dispose();
        _webGate.Dispose();
    }
}

// In-process Microsoft.AI.Foundry.Local 2.5.0 adapter (verified 0 IL warnings under NativeAOT).
internal sealed class SdkFoundryLocalApi(ILogger<SdkFoundryLocalApi> logger) : IFoundryLocalApi
{
    private const long MiB = 1024L * 1024L;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, LoadedAudioModel> _audioModels = new(StringComparer.OrdinalIgnoreCase);

    public async Task CreateManagerAsync(string? cacheDirectory, CancellationToken ct)
    {
        try
        {
            _ = FoundryLocalManager.Instance;
        }
        catch (FoundryLocalException)
        {
            var config = new Configuration
            {
                AppName = "intochat-os",
                ModelCacheDir = cacheDirectory,
                Web = new Configuration.WebService { Urls = "http://127.0.0.1:0" },
            };
            await FoundryLocalManager.CreateAsync(config, logger, ct).ConfigureAwait(false);
        }

        var manager = FoundryLocalManager.Instance;
        try
        {
            await WithTimeout(
                manager.DownloadAndRegisterEpsAsync(ct),
                DownloadTimeout,
                "Foundry execution provider registration",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogSwallowed(error, "registering Foundry Local execution providers");
        }
    }

    public async Task<IReadOnlyList<FoundryCatalogModel>> ListCatalogAsync(CancellationToken ct)
    {
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
        var models = await catalog.ListModelsAsync(ct).ConfigureAwait(false);
        return [.. models.Select(MapModel)];
    }

    public async Task<IReadOnlyList<FoundryCatalogModel>> ListCachedAsync(CancellationToken ct)
    {
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
        var models = await catalog.GetCachedModelsAsync(ct).ConfigureAwait(false);
        return [.. models.Select(MapModel)];
    }

    public async Task<IReadOnlyList<ModelId>> ListLoadedAsync(CancellationToken ct)
    {
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
        var models = await catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false);
        return [.. models.Select(static m => new ModelId(string.IsNullOrWhiteSpace(m.Alias) ? m.Id : m.Alias))];
    }

    public async Task DownloadAsync(string modelId, Action<float> onProgress, CancellationToken ct)
    {
        var model = await ResolveModelAsync(modelId, ct).ConfigureAwait(false);
        await model.DownloadAsync(onProgress, ct).ConfigureAwait(false);
    }

    public async Task LoadAsync(string modelId, CancellationToken ct)
    {
        var model = await ResolveModelAsync(modelId, ct).ConfigureAwait(false);
        await model.LoadAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string modelId, CancellationToken ct)
    {
        if (_audioModels.TryRemove(modelId, out var loaded))
        {
            await loaded.UnloadAsync(logger).ConfigureAwait(false);
        }

        var model = await ResolveModelAsync(modelId, ct).ConfigureAwait(false);
        try
        {
            await model.UnloadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, $"unloading Foundry Local model '{modelId}' before cache removal");
        }
        await model.RemoveFromCacheAsync(ct).ConfigureAwait(false);
    }

    public async Task<Uri> StartWebServiceAsync(CancellationToken ct)
    {
        var manager = FoundryLocalManager.Instance;
        await manager.StartWebServiceAsync(ct).ConfigureAwait(false);
        var urls = manager.Urls;
        if (urls is null || urls.Length == 0 || string.IsNullOrWhiteSpace(urls[0]))
        {
            throw new InvalidOperationException("Foundry Local web service started without reporting a listening URL.");
        }
        return new Uri(urls[0].TrimEnd('/') + "/v1", UriKind.Absolute);
    }

    public async Task<string> TranscribeFileAsync(string modelId, string audioFilePath, CancellationToken ct)
    {
        var loaded = _audioModels.GetOrAdd(modelId, id => new LoadedAudioModel(id, logger));
        return await loaded.TranscribeAsync(audioFilePath, ct).ConfigureAwait(false);
    }

    public async Task UnloadAsync(string modelId, CancellationToken ct)
    {
        if (_audioModels.TryRemove(modelId, out var loaded))
        {
            await loaded.UnloadAsync(logger).ConfigureAwait(false);
            return;
        }

        try
        {
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
            var model = await catalog.GetModelAsync(modelId, ct).ConfigureAwait(false)
                ?? await catalog.GetModelVariantAsync(modelId, ct).ConfigureAwait(false);
            if (model is not null)
            {
                await model.UnloadAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, $"unloading Foundry Local model '{modelId}'");
        }
    }

    private static async Task<IModel> ResolveModelAsync(string modelId, CancellationToken ct)
    {
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
        var model = await catalog.GetModelAsync(modelId, ct).ConfigureAwait(false)
            ?? await catalog.GetModelVariantAsync(modelId, ct).ConfigureAwait(false);
        return model ?? throw new ArgumentException($"Model '{modelId}' was not found in the Foundry Local catalog.");
    }

    private static FoundryCatalogModel MapModel(IModel model)
    {
        var info = model.Info;
        var ep = info.Runtime?.ExecutionProvider;
        var hasNpu = IsNpu(info.Runtime?.DeviceType.ToString(), ep)
            || model.Variants.Any(static v => IsNpu(v.Info.Runtime?.DeviceType.ToString(), v.Info.Runtime?.ExecutionProvider));
        long? bytes = info.FileSizeMb is > 0 and var mb ? mb * MiB : null;
        return new FoundryCatalogModel(
            Id: model.Id,
            Alias: model.Alias,
            Task: info.Task,
            FileSizeBytes: bytes,
            SupportsToolCalling: info.SupportsToolCalling == true,
            HasNpuVariant: hasNpu,
            ExecutionProvider: ep);
    }

    private static bool IsNpu(string? deviceType, string? executionProvider)
        => (deviceType?.Contains("NPU", StringComparison.OrdinalIgnoreCase) == true)
            || (executionProvider?.Contains("QNN", StringComparison.OrdinalIgnoreCase) == true)
            || (executionProvider?.Contains("NPU", StringComparison.OrdinalIgnoreCase) == true);

    private static async Task WithTimeout(Task task, TimeSpan timeout, string operation, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException($"{operation} timed out after {timeout.TotalMinutes:F0} minutes.");
        }
        await task.ConfigureAwait(false);
    }

    private sealed class LoadedAudioModel(string modelId, ILogger logger)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IModel? _model;
        private IModel? _cpuFallback;
        private bool _cudaFailed;

        public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct)
        {
            var active = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            try
            {
                return await RunAudioSessionAsync(active, audioFilePath, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (!_cudaFailed && IsCudaError(error))
            {
                logger.LogWarning(error, "CUDA inference failed on {ModelId}; falling back to CPU variant.", active.Id);
                var cpu = await SwitchToCpuVariantAsync(active, ct).ConfigureAwait(false);
                return await RunAudioSessionAsync(cpu, audioFilePath, ct).ConfigureAwait(false);
            }
        }

        public async Task UnloadAsync(ILogger log)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var target = _cpuFallback ?? _model;
                _cpuFallback = null;
                _model = null;
                if (target is not null)
                {
                    try
                    {
                        await target.UnloadAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        log.LogSwallowed(error, $"unloading Foundry Local audio model '{target.Id}'");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<IModel> EnsureLoadedAsync(CancellationToken ct)
        {
            if ((_cpuFallback ?? _model) is { } ready)
            {
                return ready;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if ((_cpuFallback ?? _model) is { } loaded)
                {
                    return loaded;
                }

                var model = await ResolveModelAsync(modelId, ct).ConfigureAwait(false);
                await WithTimeout(model.DownloadAsync(ct: ct), DownloadTimeout, "Foundry Local audio download", ct).ConfigureAwait(false);
                await WithTimeout(model.LoadAsync(ct), LoadTimeout, "Foundry Local audio load", ct).ConfigureAwait(false);
                _model = model;
                return model;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<IModel> SwitchToCpuVariantAsync(IModel failedModel, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cpuFallback is { } existing)
                {
                    return existing;
                }

                _cudaFailed = true;
                try
                {
                    await failedModel.UnloadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    logger.LogSwallowed(error, "unloading CUDA Foundry Local model before CPU fallback");
                }

                var cpuVariant = failedModel.Variants
                    .FirstOrDefault(static v => v.Id.Contains("generic-cpu", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"CUDA inference failed and no CPU variant was found for '{failedModel.Alias ?? failedModel.Id}'.");

                var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct).ConfigureAwait(false);
                var cpuModel = await catalog.GetModelVariantAsync(cpuVariant.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"CPU variant '{cpuVariant.Id}' was not found in the Foundry Local catalog.");

                await WithTimeout(cpuModel.DownloadAsync(ct: ct), DownloadTimeout, "Foundry Local CPU fallback download", ct).ConfigureAwait(false);
                await WithTimeout(cpuModel.LoadAsync(ct), LoadTimeout, "Foundry Local CPU fallback load", ct).ConfigureAwait(false);
                _cpuFallback = cpuModel;
                return cpuModel;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<string> RunAudioSessionAsync(IModel model, string audioFilePath, CancellationToken ct)
        {
            using var session = new AudioSession(model);
            using var request = new global::Microsoft.AI.Foundry.Local.Request();
            request.AddItem(new AudioItem(audioFilePath));
            using var response = await session.ProcessRequestAsync(request, ct).ConfigureAwait(false);
            return response
                .OfType<SpeechResultItem>()
                .Select(static item => item.Text)
                .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
                ?.Trim()
                ?? string.Empty;
        }

        private static bool IsCudaError(Exception error)
            => error.ToString().Contains("CUDNN", StringComparison.OrdinalIgnoreCase)
                || error.ToString().Contains("CUDA", StringComparison.OrdinalIgnoreCase)
                || error.GetType().Name.Contains("OnnxRuntimeGenAI", StringComparison.OrdinalIgnoreCase);
    }
}
