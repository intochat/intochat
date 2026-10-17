using DigitalBrain.AI;
using DigitalBrain.AI.Media;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using IImageGenerator = DigitalBrain.AI.Media.IImageGenerator;
using ImageGenerationRequest = DigitalBrain.AI.Media.ImageGenerationRequest;

namespace DigitalBrain.Modules.AI.Tests;

// The media neurons: images through the real OpenAI adapter against a loopback provider; embeddings,
// transcription and speech over their in-process transports, which a fact hands the neuron itself.
public sealed class MediaFacts
{
    [Fact]
    public async Task EmbeddingsRecognitionAndSynthesisUseConfiguredServices()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>()
            .ConfigureServices(services => services
                .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new Embeddings())
                .AddSingleton<IAudioTranscriptionService>(new Transcription())
                .AddSingleton<ISpeechSynthesisTransport>(new Speech())).StartAsync(ct);
        var embeddings = await brain.Get<IEmbeddingModel>("embedding").Embed(new EmbeddingRequest(["one", "two"]), ct);
        Assert.Equal(2, embeddings.Vectors.Length);
        Assert.Equal(new float[] { 3, 1 }, embeddings.Vectors[0]);
        var recognition = await brain.Get<ISpeechRecognizer>("recognition")
            .Recognize(new SpeechRecognitionRequest(new MediaPayload([4, 5], "audio/wav"), "clip.wav"), ct);
        Assert.Equal("clip.wav:2", recognition.Text);
        var speech = await brain.Get<ISpeechSynthesizer>("speech").Synthesize(new SpeechSynthesisRequest("hello", "voice"), ct);
        Assert.Equal("audio/mpeg", speech.Audio.MediaType);
        Assert.Equal(new byte[] { 5 }, speech.Audio.Content);
    }

    [Fact]
    public async Task OversizedInputIsRejectedBeforeProviderInvocation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithProvider("OpenAI", provider.Url).StartAsync(ct);
        var reached = provider.Hold(ct);
        var image = brain.Get<IImageGenerator>("invalid");
        await using var failed = await brain.Observe<MediaOperationFailed>(image, ct);
        await Assert.ThrowsAsync<ArgumentException>(() => image.Generate(new ImageGenerationRequest(new string('x', 32769)), ct));
        Assert.Equal("failed", (await failed.NextAsync(ct: ct)).Code);
        Assert.False(reached.IsCompleted);
    }

    [Fact]
    public async Task CancellationReachesTheRunningProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithProvider("OpenAI", provider.Url).StartAsync(ct);
        var neuron = brain.Get<IImageGenerator>("cancel");
        await using var failed = await brain.Observe<MediaOperationFailed>(neuron, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reached = provider.Hold(ct);
        var pending = neuron.Generate(new ImageGenerationRequest("tree"), cancellation.Token);
        await Task.WhenAny(reached, pending);
        Assert.True(reached.IsCompletedSuccessfully, "The provider was never asked.");
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("cancelled", (await failed.NextAsync(ct: ct)).Code);
    }

    [Fact]
    public async Task ImageGenerationReturnsSerializableBytesAndPublishesCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new LoopbackServer();
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithProvider("OpenAI", provider.Url).StartAsync(ct);
        var neuron = brain.Get<IImageGenerator>("image");
        await using var completed = await brain.Observe<ImageGenerated>(neuron, ct);
        var served = provider.ReplyOnce("application/json", """{"created":1,"data":[{"b64_json":"AQI="}]}""", ct);
        var result = await neuron.Generate(new ImageGenerationRequest("tree"), ct);
        using var request = System.Text.Json.JsonDocument.Parse((await served).Body);
        Assert.Equal(new byte[] { 1, 2 }, result.Image.Content);
        Assert.Equal(request.RootElement.GetProperty("model").GetString(), result.Model);
        Assert.Equal(result.OperationId, (await completed.NextAsync(ct: ct)).OperationId);
    }

    [Fact]
    public async Task SynthesisReportsUnavailableAndPublishesTypedFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        var neuron = brain.Get<ISpeechSynthesizer>("speech");
        Assert.False((await neuron.Describe()).Available);
        await using var failed = await brain.Observe<MediaOperationFailed>(neuron, ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => neuron.Synthesize(new SpeechSynthesisRequest("hello", "voice"), ct));
        Assert.Equal(MediaOperation.SpeechSynthesis, (await failed.NextAsync(ct: ct)).Operation);
    }

    private sealed class Embeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(x => new Embedding<float>(new float[] { x.Length, 1 }))));
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class Transcription : IAudioTranscriptionService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool InitializationFailed => false;
        public string? ErrorMessage => null;
        public string ModelId => "test-recognition";
        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The neuron must supply bytes, not paths.");
        public Task<string> TranscribeAsync(Stream audioStream, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult($"{fileName}:{audioStream.Length}");
    }
    private sealed class Speech : ISpeechSynthesisTransport
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public string Model => "test-speech";
        public Task<MediaPayload> Synthesize(SpeechSynthesisRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new MediaPayload([(byte)request.Text.Length], "audio/mpeg"));
    }
}
