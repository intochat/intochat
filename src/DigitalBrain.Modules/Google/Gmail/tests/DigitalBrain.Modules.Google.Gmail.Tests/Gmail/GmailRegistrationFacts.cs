using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;
namespace DigitalBrain.Modules.Google.Gmail.Tests;

public sealed class GmailRegistrationFacts
{
    [Fact]
    public async Task OAuthCredentialPairUsesOneAtomicRelease()
    {
        var integrations = new AtomicIntegrations();
        var registration = new GmailRegistration(integrations, new() { PublicOrigin = new("https://example.test/") });
        var released = await registration.ReleaseAsync();
        Assert.Equal("revision-client", released.ClientId);
        Assert.Equal("revision-secret", released.ClientSecret);
        Assert.Equal(1, integrations.Handle.Releases);
    }
    private sealed class AtomicIntegrations : IModuleIntegrations<GmailModule>
    {
        internal AtomicHandle Handle { get; } = new();
        public IIntegrationHandle Get(string integrationId) { Assert.Equal("google-oauth", integrationId); return Handle; }
    }
    private sealed class AtomicHandle : IIntegrationHandle
    {
        public string IntegrationId => "google-oauth";
        internal int Releases { get; private set; }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("A separate snapshot read could race rotation.");
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Separate field reads could mix revisions.");
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            return Task.FromResult(new ReleasedIntegration { Snapshot = new() { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready, Revision = 12 }, Values = new() { ["ClientId"] = "revision-client", ["ClientSecret"] = "revision-secret" } });
        }
    }
}
