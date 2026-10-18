using DigitalBrain.AI;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.AI.Models.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Integration.Models;

// A model neuron serves every caller as its own brain, and its key sits inside the brain it is resolved for.
public sealed class ModelAddressFacts
{
    private const string Brain = "model-address-facts";

    private static string Owner => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    [Trait("host", "own")]
    [Fact]
    public async Task AModelCallLandsOnTheModelNeuronOfTheBrainItIsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);
        await using var started = await brain.Observe<InferenceStarted>(brain.Get<IGpt56Luna>(ModelAddress.Key(Brain)), ct);

        var answer = await ModelAddress.Complete(brain.Services.GetRequiredService<IGrainFactory>(), Brain, nameof(IGpt56Luna), "Be brief.", "hello", ct);

        Assert.Equal("You said: hello", answer);
        Assert.Equal($"{Brain}/models", (await started.NextAsync(ct: ct)).NeuronId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABrainsPlainModelIsTheDeploymentDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        var scoped = await brain.Get<ILLM>(ModelAddress.Key(Brain)).Describe();

        var deployment = await brain.Get<ILLM>("default").Describe();
        Assert.Equal((deployment.Provider, deployment.Model, deployment.Profile), (scoped.Provider, scoped.Model, scoped.Profile));
    }

    [Fact]
    public void AModelKeyNeedsABrainId()
    {
        Assert.Throws<ArgumentException>(() => ModelAddress.Key($"{Brain}/apps/grocer"));
        Assert.Throws<ArgumentException>(() => ModelAddress.Key(" "));
    }

    // The owner's machine journals every signal into the part of the publisher's brain; "default" is no brain.
    [Trait("host", "own")]
    [Fact(Timeout = 120_000)]
    public async Task AModelCallOnAMachineJournalsItsSignalsInsideItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model)
            .StartAsync(ct);

        var answer = await ModelAddress.Complete(brain.Services.GetRequiredService<IGrainFactory>(), Owner, nameof(IGpt56Luna), "Be brief.", "hello", ct);

        Assert.Equal("You said: hello", answer);
        var journaled = await brain.Services.GetRequiredService<ISignalJournal>().ReadSince(Owner, 0, 1000);
        Assert.Contains(journaled, entry => entry.Signal is InferenceCompleted completed && completed.NeuronId == ModelAddress.Key(Owner));
    }
}
