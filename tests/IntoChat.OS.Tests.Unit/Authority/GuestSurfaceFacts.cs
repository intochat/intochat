using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Http;
using IdentityPosture = IntoChat.OS.Connections.Identity.Configuration.IdentityPosture;

namespace IntoChat.OS.Tests.Unit.Authority;

// A /try guest is marked by its principal alone: the try- prefix is reserved, and a session for it is never a person.
public sealed class GuestSurfaceFacts
{
    private const string Guest = "try-0a1b2c3d4e5f";

    [Theory]
    [InlineData(Guest)]
    [InlineData("try-")]
    [InlineData("try-alice")]
    public void ATryPrincipal_IsNeverAvailableToRegister(string principalId)
        => Assert.True(new PrincipalIdIsAvailable(principalId, AlreadyTaken: false).Broken);

    [Theory]
    [InlineData("alice")]
    [InlineData("trying")]
    [InlineData("retry-1")]
    [InlineData("u-0a1b2c3d4e5f")]
    public void OtherPrincipals_StayAvailable(string principalId)
        => Assert.False(new PrincipalIdIsAvailable(principalId, AlreadyTaken: false).Broken);

    [Fact]
    public async Task PasswordRegistration_RefusesATryUsername_BeforeItStoresACredential()
    {
        // No collaborator is reached: the username is refused first.
        var passwords = new PrincipalPasswords(null!, null!, null!);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => passwords.Register("try-alice", "correct-horse-battery-staple", "Alice"));
        Assert.Equal("That username is unavailable.", refused.Message);
    }

    [Fact]
    public void ASessionForATryPrincipal_ResolvesAsANonPerson()
    {
        var http = new DefaultHttpContext { User = AccountSession.SessionUser(CapabilityCallers.Of(Guest, Guest, Guest)) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.Equal(Guest, caller.PrincipalId);
        Assert.NotEqual(CallerKind.User, caller.Kind);
        Assert.IsNotType<Actor.Person>(caller.ToCaller().Actor);
    }

    [Fact]
    public void ASessionForAnyOtherPrincipal_StillResolvesAsThePerson()
    {
        var http = new DefaultHttpContext { User = AccountSession.SessionUser(CapabilityCallers.Of("alice", "alice", "owner")) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.Equal(CallerKind.User, caller.Kind);
        Assert.Null(caller.AppId);
        Assert.IsType<Actor.Person>(caller.ToCaller().Actor);
    }
}
