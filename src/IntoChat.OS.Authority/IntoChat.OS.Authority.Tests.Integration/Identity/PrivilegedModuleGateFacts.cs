using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Sdk.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// The runtime layer of the Sdk.Identity restriction: the seams resolve only for module ids listed in
// IntoChatOS:Identity:PrivilegedModules, and fail closed otherwise (also when nothing is configured).
public sealed class PrivilegedModuleGateFacts
{
    private static ServiceProvider Host(params string[] privileged)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            privileged.Select((id, index) => KeyValuePair.Create<string, string?>($"IntoChatOS:Identity:PrivilegedModules:{index}", id))).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddIdentity();
        services.AddSingleton<IIdentityClient>(new LocalIdentityClient(null!));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AnUnlistedModuleIsRefusedAndNamed()
    {
        using var host = Host("scim");
        var gate = host.GetRequiredService<IPrivilegedServices>();

        var issuer = Assert.Throws<UnauthorizedAccessException>(() => gate.For<ISessionIssuer>(new ModuleId("other")));
        var provisioning = Assert.Throws<UnauthorizedAccessException>(() => gate.For<IIdentityProvisioning>(new ModuleId("other")));

        Assert.Contains("'other'", issuer.Message, StringComparison.Ordinal);
        Assert.Contains("'other'", provisioning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListedModuleResolvesBothSeams()
    {
        using var host = Host("scim");
        var gate = host.GetRequiredService<IPrivilegedServices>();

        Assert.NotNull(gate.For<ISessionIssuer>(new ModuleId("SCIM")));
        Assert.NotNull(gate.For<IIdentityProvisioning>(new ModuleId("scim")));
    }

    [Fact]
    public void NothingConfiguredDeniesEveryModule()
    {
        using var host = Host();

        Assert.Throws<UnauthorizedAccessException>(() => host.GetRequiredService<IPrivilegedServices>().For<ISessionIssuer>(new ModuleId("scim")));
    }

    [Fact]
    public void TheSeamsAreNotResolvableFromTheContainerDirectly()
    {
        using var host = Host("scim");

        Assert.Null(host.GetService<ISessionIssuer>());
        Assert.Null(host.GetService<IIdentityProvisioning>());
    }

    [Fact]
    public void OnlyTheIdentitySeamsAreServed()
    {
        using var host = Host("scim");

        Assert.Throws<ArgumentException>(() => host.GetRequiredService<IPrivilegedServices>().For<IIdentityClient>(new ModuleId("scim")));
    }
}
