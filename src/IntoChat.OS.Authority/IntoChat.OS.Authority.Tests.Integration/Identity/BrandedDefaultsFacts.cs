using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Hosted;
using IntoChat.OS.Authority.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// Live cookies and the identity directory carry these names (decision 11): a distribution may configure the claim
// names, but the defaults stay the historical values, and the directory key is persisted state.
public sealed class BrandedDefaultsFacts
{
    [Fact]
    public void BrandedDefaultsAreConfigurableAndUnchanged()
    {
        var identity = new IdentityHostOptions();
        Assert.Equal(("intochat.account", "intochat.brain"), (identity.AccountClaim, identity.BrainClaim));
        var hosted = new HostedRuntimeOptions();
        Assert.Equal(("intochat.hosted-grant", "intochat.runtime", "intochat.cloud-user", "intochat.cloud-session"),
            (hosted.GrantClaim, hosted.RuntimeClaim, hosted.CloudUserClaim, hosted.CloudSessionClaim));
        Assert.Equal("intochat-identity-directory", IdentityGrains.Directory);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new(IdentityHostOptions.Key(nameof(IdentityHostOptions.AccountClaim)), "os.account"),
            new(IdentityHostOptions.Key(nameof(IdentityHostOptions.BrainClaim)), "os.brain"),
            new(HostedRuntimeOptions.SectionName + ":" + nameof(HostedRuntimeOptions.RuntimeClaim), "os.runtime"),
        ]).Build();
        var configured = configuration.GetSection(IdentityHostOptions.SectionName).Get<IdentityHostOptions>()!;
        Assert.Equal(("os.account", "os.brain"), (configured.AccountClaim, configured.BrainClaim));
        Assert.True(configured.Validate());
        Assert.Equal("os.runtime", configuration.GetSection(HostedRuntimeOptions.SectionName).Get<HostedRuntimeOptions>()!.RuntimeClaim);
        Assert.False(new IdentityHostOptions { BrainClaim = " " }.Validate());
    }
}
