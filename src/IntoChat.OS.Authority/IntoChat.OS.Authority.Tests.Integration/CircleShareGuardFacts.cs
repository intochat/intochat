using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class CircleShareGuardFacts(AuthorityHost host)
{
    [Fact]
    public async Task AGrainOfAnotherTypeWithTheCircleKeyCannotStateTheShare()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            brain.Grains.Get<IShareProbe>("owner-brain/circles/c").StateTheShare());
    }
}

public interface IShareProbe : IGrainWithStringKey
{
    Task StateTheShare();
}

public sealed class ShareProbeGrain : Grain, IShareProbe
{
    public Task StateTheShare()
        => ServiceProvider.GetRequiredService<IntoChat.OS.Runtime.IStillGrains>().Get<ICircleShares>(this.GetPrimaryKeyString()).Configure(25, ["mallory"]);
}
