using System.Security.Cryptography;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Microsoft.GitHub.Tests.Integration.GitHubHost))]

namespace DigitalBrain.Modules.Microsoft.GitHub.Tests.Integration;

// The shared host composes GitHub with an app registration (app 7) signed by one key generated for the assembly; the
// app-auth facts mint tokens through their own handlers. Facts that leave the app unregistered, rotate the webhook
// secret, or script the repository source, user access and installation verifier keep their own TestBrain
// ([Trait("host", "own")]).
public sealed class GitHubHost : BrainHostFixture
{
    public RSA AppKey { get; } = RSA.Create(2048);

    protected override TestBrain Configure(TestBrain brain) => AppAuthFacts.Compose(brain, AppKey.ExportPkcs8PrivateKeyPem(), "7");

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        AppKey.Dispose();
    }
}
