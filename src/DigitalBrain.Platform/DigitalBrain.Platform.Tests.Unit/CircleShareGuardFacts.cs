using DigitalBrain.Framework.Compute;
using DigitalBrain.Testing.Module;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class CircleShareGuardFacts
{
    [Fact]
    public async Task AGrainOfAnotherTypeWithTheCircleKeyCannotStateTheShare()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            brain.Grains.GetGrain<IShareProbe>("owner-brain/circles/c").StateTheShare());
    }
}

public interface IShareProbe : IGrainWithStringKey
{
    Task StateTheShare();
}

public sealed class ShareProbeGrain : Grain, IShareProbe
{
    public Task StateTheShare() => GrainFactory.GetGrain<ICircleShares>(this.GetPrimaryKeyString()).Configure(25, ["mallory"]);
}
