using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Runtimes.FoundryLocal;
using Microsoft.Extensions.AI;
using OpenAI;

namespace IntoChat.OS.Modules.Devices;

internal sealed class FoundryLocalRuntime(FoundryLocalHost host, IHardwareProbe? hardware = null) : ILocalRuntime
{
    private const string SdkVersion = "2.5.0";
    private const long GiB = 1024L * 1024L * 1024L;

    public RuntimeId Id => RuntimeId.FoundryLocal;

    public async Task<RuntimeStatus> Probe(CancellationToken ct)
    {
        try
        {
            var models = await host.ListCatalogAsync(ct).ConfigureAwait(false);
            if (models.Any(static m => m.HasNpuVariant))
            {
                hardware?.ReportNpu();
            }
            return new RuntimeStatus(Id, Reachable: true, Version: SdkVersion, Endpoint: host.WebServiceEndpoint);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, "probing Foundry Local runtime");
            return new RuntimeStatus(Id, Reachable: false, Version: null, Endpoint: null);
        }
    }

    public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyList<FoundryCatalogModel> models;
        try
        {
            models = await host.ListCatalogAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, "listing Foundry Local catalog");
            yield break;
        }

        if (models.Any(static m => m.HasNpuVariant))
        {
            hardware?.ReportNpu();
        }

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToCard(model);
        }
    }

    public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyList<FoundryCatalogModel> models;
        try
        {
            models = await host.ListCachedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, "listing cached Foundry Local models");
            yield break;
        }

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToCard(model);
        }
    }

    public async Task<IReadOnlyCollection<ModelId>> Loaded(CancellationToken ct)
    {
        try
        {
            return await host.ListLoadedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, "listing loaded Foundry Local models");
            return [];
        }
    }

    public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);

        long totalBytes = 0;
        try
        {
            var catalog = await host.ListCatalogAsync(ct).ConfigureAwait(false);
            var matched = catalog.FirstOrDefault(m =>
                string.Equals(m.Alias, id.Value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Id, id.Value, StringComparison.OrdinalIgnoreCase));
            totalBytes = matched?.FileSizeBytes ?? 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, $"reading Foundry Local model size for '{id.Value}'");
        }

        var channel = Channel.CreateUnbounded<InstallProgress>();
        double lastPercent = 0;
        long lastBytes = 0;

        var downloadTask = Task.Run(async () =>
        {
            try
            {
                // Microsoft.AI.Foundry.Local 2.5.0 reports download progress on a 0..100 float scale.
                await host.DownloadAsync(
                    id.Value,
                    rawPercent =>
                    {
                        var pct = Math.Clamp((double)rawPercent, 0d, 100d);
                        var bytes = totalBytes > 0 ? (long)Math.Round(totalBytes * pct / 100d) : 0L;
                        Volatile.Write(ref lastPercent, pct);
                        Volatile.Write(ref lastBytes, bytes);
                        channel.Writer.TryWrite(new InstallProgress(id, InstallState.Downloading, pct, bytes, totalBytes));
                    },
                    ct).ConfigureAwait(false);

                channel.Writer.TryWrite(new InstallProgress(id, InstallState.Verifying, 100, totalBytes, totalBytes));
                await host.LoadAsync(id.Value, ct).ConfigureAwait(false);
                channel.Writer.TryWrite(new InstallProgress(id, InstallState.Ready, 100, totalBytes, totalBytes));
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                OsConsoleLog.For<FoundryLocalRuntime>().LogSwallowed(error, $"installing Foundry Local model '{id.Value}'");
                channel.Writer.TryWrite(new InstallProgress(
                    id,
                    InstallState.Failed,
                    Volatile.Read(ref lastPercent),
                    Volatile.Read(ref lastBytes),
                    totalBytes,
                    error.Message));
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var progress in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return progress;
        }

        await downloadTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public Task Remove(ModelId id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        return host.RemoveAsync(id.Value, ct);
    }

    public IChatClient Chat(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        return new DeferredFoundryChatClient(host, id);
    }

    public ISpeechToTextClient? Hear(ModelId id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        return new FoundrySpeechToTextClient(host, id);
    }

    internal static ModelCard ToCard(FoundryCatalogModel model)
    {
        var key = !string.IsNullOrWhiteSpace(model.Alias) ? model.Alias : model.Id;
        var catalogued = KnownModels.Find(key) ?? KnownModels.Find(model.Id);
        if (catalogued is not null)
        {
            return model.FileSizeBytes is > 0 and var bytes ? catalogued with { Bytes = bytes } : catalogued;
        }

        var isAsr = model.Task?.Contains("speech", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(model.Task, "asr", StringComparison.OrdinalIgnoreCase)
            || key.Contains("whisper", StringComparison.OrdinalIgnoreCase);

        var abilities = isAsr
            ? Abilities.Hear
            : Abilities.Text | (model.SupportsToolCalling ? Abilities.Tools : Abilities.None);

        var tier = model.FileSizeBytes switch
        {
            > 0 and < 4L * GiB => Tier.Fast,
            > 0 and < 12L * GiB => Tier.Balanced,
            >= 12L * GiB => Tier.Deep,
            _ => Tier.Balanced,
        };

        var family = FamilyOf(key);
        return new ModelCard(
            Id: new ModelId(key),
            Provider: AiProvider.FoundryLocal,
            Family: family,
            Name: key,
            Where: Where.Device,
            Can: abilities,
            Tier: tier,
            Price: null,
            Bytes: model.FileSizeBytes);
    }

    private static string FamilyOf(string key)
    {
        var dash = key.IndexOf('-', StringComparison.Ordinal);
        var head = dash > 0 ? key[..dash] : key;
        return head.Length > 0 ? char.ToUpperInvariant(head[0]) + head[1..] : "Foundry";
    }

    private sealed class DeferredFoundryChatClient(FoundryLocalHost host, ModelId modelId) : IChatClient
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IChatClient? _inner;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner?.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            _inner?.Dispose();
            _gate.Dispose();
        }

        private async Task<IChatClient> EnsureClientAsync(CancellationToken ct)
        {
            if (_inner is { } ready)
            {
                return ready;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_inner is { } loaded)
                {
                    return loaded;
                }

                var endpoint = await host.EnsureWebServiceAsync(ct).ConfigureAwait(false);
                var created = new OpenAIClient(
                        new ApiKeyCredential("foundry-local"),
                        new OpenAIClientOptions { Endpoint = endpoint })
                    .GetChatClient(modelId.Value)
                    .AsIChatClient();
                _inner = created;
                return created;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private sealed class FoundrySpeechToTextClient(FoundryLocalHost host, ModelId modelId) : ISpeechToTextClient, IAsyncDisposable
    {
        private readonly SpeechToTextClientMetadata _metadata = new("foundry-local", null, modelId.Value);

        public async Task<SpeechToTextResponse> GetTextAsync(
            Stream audioSpeechStream,
            SpeechToTextOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audioSpeechStream);

            string? tempPath = null;
            var filePath = TryGetExistingFilePath(options);
            if (filePath is null)
            {
                tempPath = Path.Combine(Path.GetTempPath(), $"fl_hear_{Guid.NewGuid():N}.wav");
                await using (var file = File.Create(tempPath))
                {
                    await audioSpeechStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                filePath = tempPath;
            }

            try
            {
                var targetModel = !string.IsNullOrWhiteSpace(options?.ModelId) ? options.ModelId : modelId.Value;
                var text = await host.TranscribeFileAsync(targetModel, filePath, cancellationToken).ConfigureAwait(false);
                return new SpeechToTextResponse(text) { ModelId = targetModel };
            }
            finally
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
            Stream audioSpeechStream,
            SpeechToTextOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetTextAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false);
            yield return new SpeechToTextResponseUpdate(response.Text) { ModelId = response.ModelId };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is not null)
            {
                return null;
            }
            if (serviceType == typeof(SpeechToTextClientMetadata))
            {
                return _metadata;
            }
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync()
            => new(host.UnloadAsync(modelId.Value, CancellationToken.None));

        private static string? TryGetExistingFilePath(SpeechToTextOptions? options)
            => options?.AdditionalProperties is { } props
                && props.TryGetValue("filePath", out var raw)
                && raw is string path
                && !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                ? path
                : null;
    }
}
