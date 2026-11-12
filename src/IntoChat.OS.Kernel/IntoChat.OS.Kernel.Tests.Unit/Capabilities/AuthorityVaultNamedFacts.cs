using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Authority;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// 09-05, secrets@1 Named: a module's declared deployment secret reaches its module through IntoChatOS:Modules:{module}:Secrets:{secret},
// the variable the installer renders (ManifestEnvironment.SecretVariable). The runtime has already checked the manifest declares it.
public sealed class AuthorityVaultNamedFacts
{
    private static VaultPort Port(params (string Key, string Value)[] values) => new(null!, null,
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build());

    // The variable the installer renders for a module's declared secret (ManifestEnvironment.SecretVariable).
    private static string Key(string module, string secret) => DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleSection(module) + ":Secrets:" + secret;

    private static CapabilityCaller Auth { get; } = new("auth", "auth", "system", "auth");

    [Fact]
    public async Task A_configured_secret_is_set_and_resolves_for_its_module()
    {
        var port = Port((Key("auth", "signin-issuer"), "key-material"));
        var result = await port.Named(Auth, "signin-issuer:SigningKey", TestContext.Current.CancellationToken);

        Assert.True(result.Secret!.IsSet);
        Assert.StartsWith(SecretRef.NamedPrefix, result.Secret!.Reference, StringComparison.Ordinal);
        var resolved = await port.Resolve(Auth, result.Secret, TestContext.Current.CancellationToken);
        Assert.Equal("key-material", resolved.Value);
    }

    [Fact]
    public async Task A_secret_the_deployment_did_not_configure_is_unset_and_not_found()
    {
        var port = Port();
        var result = await port.Named(Auth, "signin-issuer", TestContext.Current.CancellationToken);

        Assert.False(result.Secret!.IsSet);
        Assert.Equal(SecretVaultStatus.NotFound, (await port.Resolve(Auth, result.Secret, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Another_modules_secret_is_never_read()
    {
        var port = Port((Key("other", "signin-issuer"), "not-yours"));
        var result = await port.Named(Auth, "signin-issuer", TestContext.Current.CancellationToken);

        Assert.False(result.Secret!.IsSet);
    }

    [Fact]
    public async Task A_caller_that_is_not_a_module_gets_nothing()
    {
        var port = Port((Key("auth", "signin-issuer"), "key-material"));
        var user = new CapabilityCaller("user", "account", "brain");

        Assert.False((await port.Named(user, "signin-issuer", TestContext.Current.CancellationToken)).Secret!.IsSet);
    }
}
