using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Configuration;
using IntoChat.OS.Framework.Platform.Identity;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The local profile needs no paid module: the OS composition carries no licensed module, and under the Open posture
// the account and the brain owner exist after startup without any licence being present.
public sealed class OwnerBootstrapWithoutLicenceFacts
{
    [Fact]
    public void NoModuleOfTheOsCatalogNeedsALicence()
    {
        Assert.NotEmpty(OsCatalog.Modules);
        Assert.All(OsCatalog.Modules, module => Assert.Null(module.LicenseProduct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UnderTheOpenPostureTheOwnerAccountAndBrainExistAfterStartupWithoutALicensedModule()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Open))
            .StartAsync(ct);

        var authority = brain.Platform.BrainAuthority(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id);
        var owner = await authority.Membership(AccountSession.DefaultLogin);
        Assert.NotNull(owner);
        Assert.Equal(MemberRole.Owner, owner.Role);
    }
}
