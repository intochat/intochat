using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Google;
using IntoChat.Cloud.Auth.Http;
using IntoChat.Cloud.Auth.Licensing;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Auth;

// The paid sign-in module (R4-01): email codes, TOTP, Google sign-in, sessions, licence and audit, on DigitalBrain. Its sessions
// table is the identity@1 session store (AuthIdentityProvider) and the module holds the three privileged grants the runtime checks
// before it lets any module issue, end or validate a session. Licensed: it owns sign-in. Composed only by a cloud host; the
// kernel adds it through the cloud module adapter (09-09).
[Module(Id)]
public sealed class AuthModule : Module
{
    public const string Id = "auth";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";
    // Composed only when Topology=cloud. A local kernel lists the module and does not load it.
    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    // Auth requires identity@1 (it provisions the verified principal) and secrets@1. It does not list identity@1 under Provides: the
    // runtime accepts a provider only when the module holds a neuron implementing IIdentity (ModuleCapabilityValidator) and
    // identity@1 is a single connection, which the kernel's directory already is. The module supplies the identity@1 session port
    // (AuthIdentityProvider) instead, which the cloud host binds with AddIdentitySessions (09-09); the Privileged grants below
    // are what the runtime checks before any module may use it.
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [Capability.Identity.V1, Capability.Secrets.V1],
        Needs: [Need.Http, Need.Secrets],
        Settings: [Setting.Of<AuthModuleOptions>(OptionsSection)],
        Secrets: [Secret.Named(AuthSecrets.HashingKey), Secret.Named(AuthSecrets.SigningKey), Secret.Named(AuthSecrets.GoogleClientId), Secret.Named(AuthSecrets.GoogleClientSecret)])
    {
        Privileged = [new(Capability.Identity.V1, "Issue"), new(Capability.Identity.V1, "End"), new(Capability.Identity.V1, "Validate")],
    };

    // Silo services. The host also supplies IAuthHostSeam, INeuronProxies, ISessionIssuer, IMailSender and the identity@1 and secrets@1 capabilities.
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<AuthModuleOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<AuthModuleOptions>>().Value);
        services.TryAddSingleton<AuthLicenseState>();
        services.TryAddSingleton(_ => LicenseVerifier.WithEmbeddedKeys());
        services.TryAddSingleton<AuthSecrets>();
        services.TryAddSingleton<AuthCalls>();
        services.TryAddSingleton<AuthAudit>();
        services.TryAddSingleton<AuthHashing>();
        services.TryAddSingleton<CodeMailer>();
        services.TryAddSingleton<AccountProvisioner>();
        services.TryAddSingleton<IAccountProvisioning, VerifiedAccountProvisioning>();
        services.TryAddSingleton<SessionStarter>();
        services.TryAddSingleton<LoginService>();
        services.TryAddSingleton<ExternalTicketIssuer>();
        services.TryAddSingleton<AuthIdentityProvider>();
        services.TryAddSingleton<IntoChat.Cloud.Auth.Ui.IAccountAuth, IntoChat.Cloud.Auth.Ui.UnconfiguredAccountAuth>();
        services.TryAddSingleton<IIdentitySessionPort>(provider => provider.GetRequiredService<AuthIdentityProvider>());
        services.TryAddSingleton<AuthLicenseGate>();
        GoogleWiring.Register(services);
        services.AddModuleHttpSurface<AuthHttpSurface>();
        services.AddSingleton<IHostedService, AuthStartupGuard>();
        services.AddHealthChecks().AddCheck<AuthLicenseHealthCheck>("auth-license");
    }
}
