using DigitalBrain.Flutter;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Testing.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.OS.Testing;

// The operating system's composition inside the test process: every catalog module that needs no container, the OS
// apps, a fake model, Google's token endpoint on loopback and real account sessions. The in-process target already
// supplies the master key and the csharp run-token key per session, as ReferenceBrain's private configuration does.
public static class OsTestBrain
{
    public static TestBrain Create(FakeModelServer model, TokenEndpointStub tokens) => TestBrain.Create().WithOperatingSystem(model, tokens);

    public static TestBrain WithOperatingSystem(this TestBrain brain, FakeModelServer model, TokenEndpointStub tokens)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tokens);
        return brain
            .WithModules(OsCatalog.InProcessModules)
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithFakeModel(model)
            // Google is the stub: a configured OAuth client, its token exchange and the profile Gmail reads once connected.
            .WithOptions<GmailModule, GmailModuleOptions>(gmail => gmail.ProfileEndpoint = new(tokens.Origin, "profile"))
            .WithIntegration(GmailModule.Integration, "ClientId", "os-client")
            .WithIntegration(GmailModule.Integration, "ClientSecret", "os-secret")
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail"), tokens.TokenEndpoint.AbsoluteUri)
            // Only names the redirect_uri a browser login sends; facts call the callback on the brain themselves.
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsPublicOriginKey, tokens.Origin.AbsoluteUri)
            // Accounts with cookie sessions: anonymous callers are refused and PersonAsync registers and signs in.
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Secured))
            .ConfigureServices(services =>
            {
                services.AddOperatingSystemApps();
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
    }
}
