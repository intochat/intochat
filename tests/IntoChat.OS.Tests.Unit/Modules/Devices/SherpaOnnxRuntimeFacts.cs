#pragma warning disable MEAI001 // Unit tests for MEAI text-to-speech runtime.
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class SherpaOnnxRuntimeFacts : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "intochat-sherpa-facts-" + Guid.NewGuid().ToString("N"));

    public SherpaOnnxRuntimeFacts()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup in temp directory.
        }
    }

    [Fact]
    public async Task KokoroIsInTheCatalog()
    {
        var (runtime, _, _) = CreateRuntime();
        var cards = await runtime.Catalog(Ct).ToArrayAsync(Ct);
        Assert.Contains(cards, c => c.Id == KnownModels.Kokoro82M.Id);
    }

    [Fact]
    public async Task InstallVerifiesAndThenIsReady()
    {
        var (runtime, _, _) = CreateRuntime();
        Assert.False(runtime.Ready);
        Assert.Null(runtime.Speak(KnownModels.Kokoro82M.Id));

        var states = await runtime.Install(KnownModels.Kokoro82M.Id, Ct)
            .Select(static p => p.State)
            .ToArrayAsync(Ct);

        Assert.Equal([InstallState.Downloading, InstallState.Verifying, InstallState.Ready], states.Distinct().ToArray());
        Assert.True(runtime.Ready);
        Assert.NotNull(runtime.Speak(KnownModels.Kokoro82M.Id));
        var installed = await runtime.Installed(Ct).ToArrayAsync(Ct);
        Assert.Contains(installed, c => c.Id == KnownModels.Kokoro82M.Id);
    }

    [Fact]
    public async Task ABadChecksumFailsAndLeavesNoFolder()
    {
        var (runtime, dir, _) = CreateRuntime(tamperArchive: true);

        var last = await runtime.Install(KnownModels.Kokoro82M.Id, Ct).LastAsync(Ct);

        Assert.Equal(InstallState.Failed, last.State);
        Assert.NotNull(last.Error);
        Assert.Contains("checksum", last.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(dir, SherpaModels.KokoroDirectoryName)));
        Assert.False(File.Exists(Path.Combine(dir, SherpaModels.VadFileName)));
        Assert.False(runtime.Ready);
    }

    [Fact]
    public async Task SpeakReturnsPcmAt24k()
    {
        var (runtime, _, api) = CreateRuntime();
        await foreach (var _ in runtime.Install(KnownModels.Kokoro82M.Id, Ct)) { }

        var client = runtime.Speak(KnownModels.Kokoro82M.Id);
        Assert.NotNull(client);

        var response = await client!.GetAudioAsync(
            "Hi.",
            new TextToSpeechOptions { VoiceId = "af_heart", Speed = 1.15f },
            Ct);
        var audio = Assert.Single(response.Contents.OfType<DataContent>());

        Assert.Equal("audio/pcm;rate=24000", audio.MediaType);
        Assert.Equal(api.Samples * 2, audio.Data.Length);
        Assert.Equal(3, api.LastSpeakerId);
        Assert.Equal(1.15f, api.LastSpeed);

        var streamUpdates = await client.GetStreamingAudioAsync("First sentence. Second sentence!", null, Ct).ToArrayAsync(Ct);
        Assert.Equal(2, streamUpdates.Length);
        Assert.Equal(SherpaModels.DefaultSpeakerId, api.LastSpeakerId);
    }

    [Fact]
    public async Task VadReportsAnEndedSegment()
    {
        var (runtime, _, _) = CreateRuntime();
        await foreach (var _ in runtime.Install(KnownModels.Kokoro82M.Id, Ct)) { }

        using var vad = runtime.Open();
        var duringSpeech = vad.Push(TonePcm16(600, 16_000));
        var afterSilence = vad.Push(SilencePcm16(500, 16_000));

        Assert.True(duringSpeech.Speaking);
        Assert.True(duringSpeech.SpeakingFor >= TimeSpan.FromMilliseconds(500));
        Assert.Empty(duringSpeech.Ended);

        Assert.False(afterSilence.Speaking);
        Assert.Equal(TimeSpan.Zero, afterSilence.SpeakingFor);
        var ended = Assert.Single(afterSilence.Ended);
        Assert.True(ended.Length > 0);
    }

    [Fact]
    public void ChatIsNotServed()
    {
        var (runtime, _, _) = CreateRuntime();
        Assert.Throws<NotSupportedException>(() => runtime.Chat(KnownModels.Kokoro82M.Id));
    }

    [Fact]
    public async Task RemoveDeletesOnlyTheModelArtifacts()
    {
        var (runtime, dir, _) = CreateRuntime();
        await foreach (var _ in runtime.Install(KnownModels.Kokoro82M.Id, Ct)) { }
        var unrelated = Path.Combine(dir, "keep-me.txt");
        await File.WriteAllTextAsync(unrelated, "keep", Ct);

        await runtime.Remove(KnownModels.Kokoro82M.Id, Ct);

        Assert.False(runtime.Ready);
        Assert.False(Directory.Exists(Path.Combine(dir, SherpaModels.KokoroDirectoryName)));
        Assert.False(File.Exists(Path.Combine(dir, SherpaModels.VadFileName)));
        Assert.True(File.Exists(unrelated));
    }

    private (SherpaOnnxRuntime Runtime, string ModelDir, FakeSherpaApi Api) CreateRuntime(bool tamperArchive = false)
    {
        var modelDir = Path.Combine(_tempRoot, "models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelDir);

        var (archiveBytes, archiveSha) = BuildTinyKokoroArchive();
        var vadBytes = "fake-silero-vad-onnx"u8.ToArray();
        var vadSha = Convert.ToHexStringLower(SHA256.HashData(vadBytes));

        var servedArchive = tamperArchive ? "corrupted-archive"u8.ToArray() : archiveBytes;
        var assets = new SherpaModelAssets(
            new Uri("https://example.test/kokoro-multi-lang-v1_0.tar.bz2"),
            archiveBytes.Length,
            archiveSha,
            new Uri("https://example.test/silero_vad_v5.onnx"),
            vadBytes.Length,
            vadSha);

        var handler = new MapHttpHandler(new Dictionary<Uri, byte[]>
        {
            [assets.KokoroArchiveUrl] = servedArchive,
            [assets.VadUrl] = vadBytes,
        });

        var options = new DevicesOptions
        {
            SherpaOnnx = new SherpaOnnxRuntimeOptions
            {
                Enabled = true,
                ModelDirectory = modelDir,
            },
        };

        var api = new FakeSherpaApi();
        var runtime = new SherpaOnnxRuntime(
            options,
            api,
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<SherpaOnnxRuntime>.Instance,
            assets);

        return (runtime, modelDir, api);
    }

    private (byte[] Bytes, string Sha256) BuildTinyKokoroArchive()
    {
        var stage = Path.Combine(_tempRoot, "pack-" + Guid.NewGuid().ToString("N"));
        var kokoroDir = Path.Combine(stage, SherpaModels.KokoroDirectoryName);
        Directory.CreateDirectory(kokoroDir);
        File.WriteAllText(Path.Combine(kokoroDir, "model.onnx"), "fake-kokoro-onnx");
        File.WriteAllText(Path.Combine(kokoroDir, "voices.bin"), "fake-voices");
        File.WriteAllText(Path.Combine(kokoroDir, "tokens.txt"), "a 1\n");
        File.WriteAllText(Path.Combine(kokoroDir, "lexicon-us-en.txt"), "hi h a I\n");

        var archivePath = Path.Combine(stage, SherpaModels.KokoroArchiveName);
        var psi = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-cjf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(stage);
        psi.ArgumentList.Add(SherpaModels.KokoroDirectoryName);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start tar.");
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);

        var bytes = File.ReadAllBytes(archivePath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (bytes, sha);
    }

    private static byte[] TonePcm16(int durationMs, int sampleRate)
    {
        var count = sampleRate * durationMs / 1000;
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 16_000);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] SilencePcm16(int durationMs, int sampleRate)
        => new byte[sampleRate * durationMs / 1000 * 2];

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MapHttpHandler(IReadOnlyDictionary<Uri, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && files.TryGetValue(request.RequestUri, out var payload))
            {
                var content = new ByteArrayContent(payload);
                content.Headers.ContentLength = payload.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeSherpaApi : ISherpaApi
    {
        public int Samples { get; } = 480;
        public int LastSpeakerId { get; private set; } = -1;
        public float LastSpeed { get; private set; }

        public ISherpaTts CreateTts(string modelDirectory) => new FakeTts(this);

        public ISherpaVad CreateVad(string modelPath, int sampleRate) => new FakeVad(sampleRate);

        private sealed class FakeTts(FakeSherpaApi owner) : ISherpaTts
        {
            public int SampleRate => SherpaModels.KokoroSampleRate;

            public float[] Synthesize(string text, float speed, int speakerId, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                owner.LastSpeakerId = speakerId;
                owner.LastSpeed = speed;
                var data = new float[owner.Samples];
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = 0.25f;
                }

                return data;
            }

            public void Dispose() { }
        }

        private sealed class FakeVad(int sampleRate) : ISherpaVad
        {
            private readonly List<float> _current = [];
            private readonly Queue<float[]> _segments = new();
            private bool _speaking;
            private int _silentSamples;

            public int SampleRate => sampleRate;
            public int WindowSize => 512;

            public void AcceptWaveform(float[] samples)
            {
                var energy = samples.Sum(static s => MathF.Abs(s)) / Math.Max(1, samples.Length);
                if (energy > 0.05f)
                {
                    _speaking = true;
                    _silentSamples = 0;
                    _current.AddRange(samples);
                }
                else if (_speaking)
                {
                    _silentSamples += samples.Length;
                    _current.AddRange(samples);
                    if (_silentSamples >= (int)(sampleRate * 0.4))
                    {
                        _segments.Enqueue([.. _current]);
                        _current.Clear();
                        _speaking = false;
                        _silentSamples = 0;
                    }
                }
            }

            public bool IsSpeechDetected() => _speaking;

            public bool IsEmpty() => _segments.Count == 0;

            public float[] PopSegment() => _segments.Dequeue();

            public void Flush()
            {
                if (_current.Count > 0)
                {
                    _segments.Enqueue([.. _current]);
                    _current.Clear();
                    _speaking = false;
                }
            }

            public void Dispose() { }
        }
    }
}
