using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The local profile needs no paid module: the OS composition carries no licensed module, and under the Open posture
// the account and the brain owner exist after startup without any licence being present.
public sealed class OwnerBootstrapWithoutLicenceFacts
{
    [Fact]
    public void NoModuleOfTheOsCatalogNeedsALicence()
    {
        Assert.NotEmpty(OperatingSystemModules.Modules);
        Assert.All(OperatingSystemModules.Modules, module => Assert.Null(module.LicenseProduct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UnderTheOpenPostureTheOwnerAccountAndBrainExistAfterStartupWithoutALicensedModule()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Open))
            .StartAsync(ct);

        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var account = await identity.ReadAccount(AccountSession.DefaultLogin);
        Assert.NotNull(account);
        Assert.Equal(AccountSession.DefaultLogin, account.OwnerPrincipalId);
        Assert.NotEmpty(await identity.BrainsOf(AccountSession.DefaultLogin));
    }
}
