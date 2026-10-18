using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Authority.Tests.Integration;

public sealed class CircleShareGuardFacts
{
    [Fact]
    public async Task AGrainOfAnotherTypeWithTheCircleKeyCannotStateTheShare()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);

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
