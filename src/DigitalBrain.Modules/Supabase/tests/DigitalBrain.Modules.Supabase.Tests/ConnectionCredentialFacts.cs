using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Supabase;
using DigitalBrain.Framework.Types;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Supabase.Tests;

public sealed class ConnectionCredentialFacts
{
    [Fact]
    public async Task AUserQueryRequiresItsConnectionAndDoesNotFallBackToInfrastructure()
    {
        var credentials = new MissingConnection();
        var provider = new ConnectionSupabaseProvider(credentials);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.QueryAsync("select 1", 1, [], ct));
        Assert.Equal(("supabase", "supabase", ct), credentials.Request);
    }

    [Fact]
    public async Task SameNamedConnectionsResolveTheProjectCredentialForTheCurrentBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<SupabaseModule>()
            .StartAsync(ct);
        var credentials = brain.SiloServices.GetRequiredService<IModuleConnectionCredentials<SupabaseModule>>();
        static void Stamp(string name) => CallerContextStamper.Stamp(new()
        {
            PrincipalId = "owner",
            AccountId = "account-owner",
            BrainId = name,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        async Task Connect(string name, string value)
        {
            Stamp(name);
            await brain.AuthorizeCallerAsync();
            await brain.Get<IConnectionRegistry>(BrainScope.CurrentId()).Connect(new()
            {
                IntegrationId = "supabase",
                ConnectionId = "supabase",
                AuthMethodId = "connection-string",
                Value = value,
            }, ct);
        }
        try
        {
            await Connect("one", "Host=one;Database=one;Username=reader");
            await Connect("two", "Host=two;Database=two;Username=reader");
            Stamp("two");
            Assert.Equal("Host=two;Database=two;Username=reader", await credentials.ReadConnectionAsync("supabase", "supabase", ct));
            CallerContextStamper.Stamp(CallerContextStamper.Require() with { BrainId = "one" });
            Assert.Equal("Host=one;Database=one;Username=reader", await credentials.ReadConnectionAsync("supabase", "supabase", ct));
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    private sealed class MissingConnection : IModuleConnectionCredentials<SupabaseModule>
    {
        public (string, string, CancellationToken) Request { get; private set; }
        public Task<string> ReadConnectionAsync(string connectorId, string connectionId, CancellationToken cancellationToken = default)
        {
            Request = (connectorId, connectionId, cancellationToken);
            throw new UnauthorizedAccessException("The brain has no connected project.");
        }
        public Task ConnectAsync(string connectionKey, string authMethodId, SecretRef credential, CancellationToken cancellationToken = default, string? targetBrainId = null) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(string connectionKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SecretRef> WriteSecretAsync(string connectionKey, string field, string displayName, string value, CancellationToken cancellationToken = default, string? targetBrainId = null)
            => throw new NotSupportedException();
    }
}
