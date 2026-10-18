using DigitalBrain.Flutter;
using DigitalBrain.Framework.Configuration;
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
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail"), tokens.TokenEndpoint.AbsoluteUri)
            // Accounts with cookie sessions: anonymous callers are refused and PersonAsync registers and signs in.
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Secured))
            .ConfigureServices(services =>
            {
                services.AddOperatingSystemApps();
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
    }
}
