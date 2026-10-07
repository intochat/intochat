using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class PublisherDirectoryFacts
{
    [Fact]
    public async Task APublisherResolvesToItsAccountsDefaultBrainScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var member = await brain.Get<IIdentityDirectory>(IdentityGrains.Directory).RegisterAsync("intochat", "correct-password", "IntoChat", ct);

        var publisher = await brain.SiloServices.GetRequiredService<IPublisherDirectory>().Resolve("intochat", ct);

        Assert.Equal(new PublisherBrain("intochat", member.AccountId, member.BrainId), publisher);
        Assert.Equal(BrainScope.Create(member.AccountId, member.BrainId).Id, publisher.ScopeId);
    }

    [Fact]
    public async Task AnUnknownPublisherIsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            brain.SiloServices.GetRequiredService<IPublisherDirectory>().Resolve("nobody", ct));
    }
}
