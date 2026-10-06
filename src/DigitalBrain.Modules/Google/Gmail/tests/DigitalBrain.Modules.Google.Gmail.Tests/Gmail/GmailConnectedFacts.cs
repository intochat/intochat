using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

public sealed class GmailConnectedFacts
{
    [Fact]
    public async Task AuthorizationCodePublishesGmailConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<GmailModule>()
            .WithExecution(new() { PrivateConfiguration = GmailHostFixture.ConfiguredRegistration() })
            .ConfigureSilo(silo => silo.Services.AddSingleton<IGmailTokenExchange>(new FakeGmailTokens()))
            .StartAsync(ct);
        CallerContextStamper.Stamp(new DigitalBrain.Enforcement.CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = DigitalBrain.Enforcement.CallerKind.User, StampedBy = DigitalBrain.Enforcement.TrustedEdge.AuthenticatedHttp });
        var gmail = brain.Get<IGmail>(ConnectionKeys.For(BrainScope.CurrentId(), "gmail", "primary"));
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        await using var connected = await brain.Observe<GmailConnected>(gmail, ct);
        CallerContextStamper.Stamp(new DigitalBrain.Enforcement.CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = DigitalBrain.Enforcement.CallerKind.User, StampedBy = DigitalBrain.Enforcement.TrustedEdge.AuthenticatedHttp });
        await brain.Grains.GetGrain<IGmailInbound>(gmail.GetGrainId()).AcceptAuthorizationCode("fake-code", "owner");
        Assert.Equal("user@gmail.com", (await connected.NextAsync(ct: ct)).EmailAddress);

    }
}

internal sealed class FakeGmailTokens : IGmailTokenExchange
{
    public Task<GmailTokenGrant> ExchangeAsync(string refreshToken, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<GmailTokenGrant> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken)
        => Task.FromResult(new GmailTokenGrant("access-token", "refresh-token", GmailScopes.Read, 3600, "user@gmail.com"));
}
