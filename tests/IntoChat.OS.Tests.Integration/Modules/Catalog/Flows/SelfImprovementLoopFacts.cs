using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Modules.Catalog.Flows;

public sealed class SelfImprovementLoopFacts
{
    private static readonly Type[] LoopModules = [.. Journey.SliceModules, typeof(UiModule)];

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task UnboundRequiredSlotRefusesActivationNamingTheSlot(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, LoopModules, f => f.Friends().Postgres(), ct);
        var ada = os.Person("ada");

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => loop.Status(ct));
        Assert.Equal(ActivationRefusedException.SlotUnbound, refused.Reason);
        Assert.Contains("findingsLog", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task BoundLoopActivatesAndReportsReady(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(kind, LoopModules, f => f.Friends().Postgres(), ct);
        var ada = os.Person("ada");

        var findingsTableId = new NeuronId(ada.Brain, "ui.table", "loop-findings");
        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("loop.self-improvement"),
            [new Binding("findingsLog", new BindingTarget.Local(findingsTableId))],
            ct);

        var loop = ada.Get<ISelfImprovementLoop>(
            new NeuronId(ada.Brain, "loop.self-improvement", ISelfImprovementLoop.DefaultKey));

        var status = await loop.Status(ct);
        Assert.Equal("ready", status);
    }
}
