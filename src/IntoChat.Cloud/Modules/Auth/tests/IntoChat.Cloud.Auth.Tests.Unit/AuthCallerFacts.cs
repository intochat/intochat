using System.Security.Claims;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class AuthCallerFacts
{
    [Fact]
    public void Require_WithNullHttp_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AuthCaller.Require(null!));
    }

    [Fact]
    public void Create_WithNullPrincipal_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AuthCaller.Create(null!));
    }

    [Fact]
    public void Create_WithUnauthenticatedPrincipal_ThrowsUnauthorizedAccessException()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Throws<UnauthorizedAccessException>(() => AuthCaller.Create(principal));
    }

    [Fact]
    public void Create_WithAuthenticatedUserMissingPrincipalClaim_ThrowsUnauthorizedAccessException()
    {
        var identity = new ClaimsIdentity("TestAuth");
        var principal = new ClaimsPrincipal(identity);
        Assert.Throws<UnauthorizedAccessException>(() => AuthCaller.Create(principal));
    }

    [Fact]
    public void Create_WithPrincipalOnly_FallsBackAccountAndBrainToPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim(SessionClaims.Principal, "alice")], "TestAuth");
        var caller = AuthCaller.Create(new ClaimsPrincipal(identity));

        Assert.Equal("alice", caller.PrincipalId);
        Assert.Equal("alice", caller.AccountId);
        Assert.Equal("alice", caller.BrainId);
    }

    [Fact]
    public void Create_WithExplicitAccountAndBrain_PreservesExactValues()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(SessionClaims.Principal, "alice"),
            new Claim(SessionClaims.Account, "acc-1"),
            new Claim(SessionClaims.Brain, "brain-1"),
        ], "TestAuth");
        var caller = AuthCaller.Create(new ClaimsPrincipal(identity));

        Assert.Equal("alice", caller.PrincipalId);
        Assert.Equal("acc-1", caller.AccountId);
        Assert.Equal("brain-1", caller.BrainId);
    }

    [Fact]
    public void Create_WithEmptyAccountAndBrainClaims_FallsBackToPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(SessionClaims.Principal, "alice"),
            new Claim(SessionClaims.Account, ""),
            new Claim(SessionClaims.Brain, ""),
        ], "TestAuth");
        var caller = AuthCaller.Create(new ClaimsPrincipal(identity));

        Assert.Equal("alice", caller.PrincipalId);
        Assert.Equal("alice", caller.AccountId);
        Assert.Equal("alice", caller.BrainId);
    }
}
