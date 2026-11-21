#pragma warning disable MEAI001
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Runtimes.FoundryLocal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class FoundryLocalRuntimeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StaticOptions(DevicesOptions value) : IOptionsMonitor<DevicesOptions>
    {
        public DevicesOptions CurrentValue => value;
        public DevicesOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DevicesOptions, string?> listener) => null;
    }

    private sealed class FakeFoundryApi : IFoundryLocalApi
    {
        public int Creates { get; private set; }
        public int WebStarts { get; private set; }
        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        public string? LastCacheDirectory { get; private set; }
        public string? LastTranscribedModel { get; private set; }
        public float[] DownloadSteps { get; set; } = [0f, 50f, 100f];
        public List<FoundryCatalogModel> CatalogModels { get; } =
        [
            new("phi-4-mini-instruct-generic-cpu:4", "phi-4-mini", "chat-completion", 2_500_000_000, SupportsToolCalling: true, HasNpuVariant: false, ExecutionProvider: "CPUExecutionProvider"),
            new("whisper-large-v3-turbo-generic-cpu:1", "whisper-large-v3-turbo", "automatic-speech-recognition", 1_600_000_000, SupportsToolCalling: false, HasNpuVariant: false, ExecutionProvider: "CPUExecutionProvider"),
        ];
        public List<FoundryCatalogModel> CachedModels { get; } = [];

        public Task CreateManagerAsync(string? cacheDirectory, CancellationToken ct)
        {
            Creates++;
            LastCacheDirectory = cacheDirectory;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FoundryCatalogModel>> ListCatalogAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FoundryCatalogModel>>([.. CatalogModels]);

        public Task<IReadOnlyList<FoundryCatalogModel>> ListCachedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FoundryCatalogModel>>([.. CachedModels]);

        public Task<IReadOnlyList<ModelId>> ListLoadedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelId>>([]);

        public Task DownloadAsync(string modelId, Action<float> onProgress, CancellationToken ct)
        {
            foreach (var step in DownloadSteps)
            {
                ct.ThrowIfCancellationRequested();
                onProgress(step);
            }
            return Task.CompletedTask;
        }

        public Task LoadAsync(string modelId, CancellationToken ct)
        {
            Loads++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string modelId, CancellationToken ct)
        {
            CachedModels.RemoveAll(m => m.Alias == modelId || m.Id == modelId);
            return Task.CompletedTask;
        }

        public Task<Uri> StartWebServiceAsync(CancellationToken ct)
        {
            WebStarts++;
            return Task.FromResult(new Uri("http://127.0.0.1:56274/v1"));
        }

        public Task<string> TranscribeFileAsync(string modelId, string audioFilePath, CancellationToken ct)
        {
            LastTranscribedModel = modelId;
            return Task.FromResult("transcribed text");
        }

        public Task UnloadAsync(string modelId, CancellationToken ct)
        {
            Unloads++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHardwareProbe : IHardwareProbe
    {
        private bool _npu;

        public Hardware Read() => _npu
            ? new Hardware(16L * 1024 * 1024 * 1024, 0, [Accelerator.Cpu, Accelerator.Npu])
            : new Hardware(16L * 1024 * 1024 * 1024, 0, [Accelerator.Cpu]);

        public void ReportNpu() => _npu = true;
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(Ct))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task DownloadProgressBecomesInstallProgress()
    {
        var fake = new FakeFoundryApi { DownloadSteps = [0f, 50f, 100f] };
        using var host = new FoundryLocalHost(fake, new StaticOptions(new DevicesOptions()));
        var runtime = new FoundryLocalRuntime(host);

        var p = await Collect(runtime.Install(new ModelId("phi-4-mini"), Ct));
        Assert.Equal([0d, 50d, 100d], p.Where(static x => x.State == InstallState.Downloading).Select(static x => x.Percent));
        Assert.Contains(p, static x => x.State == InstallState.Verifying);
        Assert.Equal(InstallState.Ready, p[^1].State);
        Assert.Equal(1, fake.Loads);
    }

    [Fact]
    public async Task WhisperIsAHearCard()
    {
        var fake = new FakeFoundryApi();
        using var host = new FoundryLocalHost(fake, new StaticOptions(new DevicesOptions()));
        var runtime = new FoundryLocalRuntime(host);

        var cards = await Collect(runtime.Catalog(Ct));
        var whisper = Assert.Single(cards, static c => c.Id.Value == "whisper-large-v3-turbo");
        Assert.True(whisper.Can.HasFlag(Abilities.Hear));
        Assert.Equal(AiProvider.FoundryLocal, whisper.Provider);
    }

    [Fact]
    public async Task OneManagerPerProcess()
    {
        var fake = new FakeFoundryApi();
        using var host = new FoundryLocalHost(fake, new StaticOptions(new DevicesOptions()));

        await host.Manager(Ct);
        await host.Manager(Ct);

        Assert.Equal(1, fake.Creates);
    }

    [Fact]
    public async Task ProbeReportsNpuWhenCatalogHasNpuVariant()
    {
        var fake = new FakeFoundryApi();
        fake.CatalogModels.Add(new("phi-4-mini-qnn-npu:1", "phi-4-mini-npu", "chat-completion", 2_000_000_000, true, HasNpuVariant: true, ExecutionProvider: "QNNExecutionProvider"));
        var hw = new RecordingHardwareProbe();
        using var host = new FoundryLocalHost(fake, new StaticOptions(new DevicesOptions()));
        var runtime = new FoundryLocalRuntime(host, hw);

        var status = await runtime.Probe(Ct);
        Assert.True(status.Reachable);
        Assert.Contains(Accelerator.Npu, hw.Read().Accelerators);
    }

    [Fact]
    public async Task HearClientTranscribesThroughFoundryHost()
    {
        var fake = new FakeFoundryApi();
        using var host = new FoundryLocalHost(fake, new StaticOptions(new DevicesOptions()));
        var runtime = new FoundryLocalRuntime(host);

        var hear = runtime.Hear(KnownModels.WhisperLargeV3Turbo.Id);
        Assert.NotNull(hear);
        using var audio = new MemoryStream([1, 2, 3, 4]);
        var response = await hear!.GetTextAsync(audio, cancellationToken: Ct);
        Assert.Equal("transcribed text", response.Text);
        Assert.Equal("whisper-large-v3-turbo", fake.LastTranscribedModel);

        await ((IAsyncDisposable)hear).DisposeAsync();
        Assert.Equal(1, fake.Unloads);
    }

    [Fact]
    public void TheAiModuleNoLongerReferencesFoundryLocal() =>
        Assert.DoesNotContain(
            typeof(AIModule).Assembly.GetReferencedAssemblies(),
            static a => a.Name!.StartsWith("Microsoft.AI.Foundry", StringComparison.Ordinal));
}
