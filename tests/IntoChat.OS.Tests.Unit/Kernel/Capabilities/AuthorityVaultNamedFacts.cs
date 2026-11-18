using DigitalBrain;
using DigitalBrain.Access;
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
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build(), callers: TestOS.Callers);

    // The variable the installer renders for a module's declared secret (ManifestEnvironment.SecretVariable).
    private static string Key(string module, string secret) => DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleSection(module) + ":Secrets:" + secret;

    private static readonly DigitalBrain.Testing.TestOS TestOS = DigitalBrain.Testing.TestOS.Local();

    private static Caller Auth { get; } = TestOS.Edge.Module("auth", new BrainId("system"));

    private static IDisposable As(Caller caller) => TestOS.Edge.Enter(caller);

    [Fact]
    public async Task A_configured_secret_is_set_and_resolves_for_its_module()
    {
        var port = Port((Key("auth", "signin-issuer"), "key-material"));
        using var _ = As(Auth);
        var result = await port.Named("signin-issuer:SigningKey", TestContext.Current.CancellationToken);

        Assert.True(result.Secret!.IsSet);
        Assert.StartsWith(SecretRef.NamedPrefix, result.Secret!.Reference, StringComparison.Ordinal);
        var resolved = await port.Resolve(result.Secret, TestContext.Current.CancellationToken);
        Assert.Equal("key-material", resolved.Value);
    }

    [Fact]
    public async Task A_secret_the_deployment_did_not_configure_is_unset_and_not_found()
    {
        var port = Port();
        using var _ = As(Auth);
        var result = await port.Named("signin-issuer", TestContext.Current.CancellationToken);

        Assert.False(result.Secret!.IsSet);
        Assert.Equal(SecretVaultStatus.NotFound, (await port.Resolve(result.Secret, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Another_modules_secret_is_never_read()
    {
        var port = Port((Key("other", "signin-issuer"), "not-yours"));
        using var _ = As(Auth);
        var result = await port.Named("signin-issuer", TestContext.Current.CancellationToken);

        Assert.False(result.Secret!.IsSet);
    }

    [Fact]
    public async Task A_caller_that_is_not_a_module_gets_nothing()
    {
        var port = Port((Key("auth", "signin-issuer"), "key-material"));
        using var _ = As(TestOS.Edge.Person("user", "account", new BrainId("brain")));

        Assert.False((await port.Named("signin-issuer", TestContext.Current.CancellationToken)).Secret!.IsSet);
    }
}
