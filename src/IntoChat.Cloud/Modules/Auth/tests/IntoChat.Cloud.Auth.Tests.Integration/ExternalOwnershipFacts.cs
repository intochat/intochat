using IntoChat.Cloud.Auth.Neurons;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class ExternalOwnershipFacts(AuthHost host)
{
    [Fact]
    public async Task StaleExternalInitialization_CannotOverwriteMovedEmail()
    {
        using var user = await host.SignUpAsync();
        var original = host.EmailHash(user.Email);
        var moved = host.EmailHash(AuthHost.UniqueEmail("moved-external"));
        Assert.True(await host.Credential(user.PrincipalId).TryMoveEmailHash(original, moved));
        await host.Credential(user.PrincipalId).SetEmailHash(original);
        Assert.Equal(moved, await host.Credential(user.PrincipalId).GetEmailHash());
    }

    [Fact]
    public async Task StaleExternalVerification_CannotRecreateReleasedIndex()
    {
        using var user = await host.SignUpAsync();
        await host.Index(user.Email).Release(user.PrincipalId);
        await host.Index(user.Email).MarkVerified(user.PrincipalId, user.AccountId, user.BrainId);
        Assert.Equal(EmailIndexStatus.None, (await host.Index(user.Email).Get()).Status);
    }
}
