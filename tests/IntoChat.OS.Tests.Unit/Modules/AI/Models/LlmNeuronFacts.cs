using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class LlmNeuronFacts
{
    private static readonly InferenceRequest Hello = new([new AiMessage("user", [new AiText("hello")])]);

    private sealed class RecordingInference : IModelInference
    {
        public ModelId? LastModel { get; private set; }
        public ModelSelection? LastSelection { get; private set; }
        public string? LastBrain { get; private set; }

        public bool HasProfile(string name) => false;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            LastSelection = selection;
            LastModel = model;
            LastBrain = brainId;
            return Task.FromResult(new ModelDescriptor("Fake", model?.Value ?? "default", null, "rev",
                CapabilitySupport.Supported, CapabilitySupport.Unknown, CapabilitySupport.Unknown));
        }

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            LastSelection = request.Model;
            LastModel = model;
            LastBrain = brainId;
            return Task.FromResult(new InferenceResult(
                [new AiMessage("assistant", [new AiText("ok")])],
                "stop",
                new InferenceUsage(1, 1, 2),
                "resp-1",
                model?.Value ?? "default"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            ModelId? model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            string? brainId)
        {
            LastSelection = request.Model;
            LastModel = model;
            LastBrain = brainId;
            await Task.Yield();
            yield return new InferenceUpdate("assistant", [new AiText("ok")], "stop", new InferenceUsage(1, 1, 2), "resp-1", "msg-1", model?.Value ?? "default");
        }
    }

    [Fact]
    public async Task TheKeyNamesTheModel()
    {
        var fake = new RecordingInference();
        var services = new ServiceCollection();
        services.AddSingleton<IModelInference>(fake);
        await using var sp = services.BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp);
        os.Register(typeof(AIModule).Assembly);
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));

        var llm = os.Get<ILLM>(PlainNeuronContracts.IdOf(typeof(ILLM), ILLM.Key("owner", KnownModels.Gpt56Luna.Id)));
        await llm.Generate(Hello, TestContext.Current.CancellationToken);

        Assert.Equal(KnownModels.Gpt56Luna.Id, fake.LastModel);
        Assert.Equal("owner", fake.LastBrain);
    }

    [Fact]
    public async Task TheBrainKeyMeansTheChatRole()
    {
        var fake = new RecordingInference();
        var services = new ServiceCollection();
        services.AddSingleton<IModelInference>(fake);
        await using var sp = services.BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp);
        os.Register(typeof(AIModule).Assembly);
        using var caller = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));

        await os.Get<ILLM>(PlainNeuronContracts.IdOf(typeof(ILLM), ModelAddress.Key("owner"))).Generate(Hello, TestContext.Current.CancellationToken);

        Assert.Null(fake.LastModel);
        Assert.Null(fake.LastSelection);
    }

    [Fact]
    public void NoMarkerTypesRemain() =>
        Assert.DoesNotContain(
            typeof(ILLM).Assembly.GetTypes(),
            static t => t.IsInterface && t != typeof(ILLM) && typeof(ILLM).IsAssignableFrom(t));
}
