using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class RuntimeSignalFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SiloSubscriptionsReceiveOrdinaryNeuronSignalsAndFilterByType()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var signals = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<Number>();
        await brain.Get<ITestEmitter>("one").EmitText("ignored");
        await brain.Get<ITestEmitter>("one").Emit(1);
        await brain.Get<ITestEmitter>("two").Emit(2);
        Assert.Equal(1, (await signals.Reader.ReadAsync(ct)).Value);
        Assert.Equal(2, (await signals.Reader.ReadAsync(ct)).Value);
        Assert.False(signals.Reader.TryRead(out _));
        signals.Dispose();
        await brain.Get<ITestEmitter>("one").Emit(3);
        await signals.Reader.Completion;
    }
}
