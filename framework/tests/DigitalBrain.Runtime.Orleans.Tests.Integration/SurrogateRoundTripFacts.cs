using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

public interface ISurrogateEchoGrain : IGrainWithStringKey
{
    Task<ConnectionView> EchoConnectionView(ConnectionView value);
    Task<OAuthGrant> EchoOAuthGrant(OAuthGrant value);
    Task<ConnectionProbeResult> EchoProbeResult(ConnectionProbeResult value);
    Task<IntegrationSnapshot> EchoIntegrationSnapshot(IntegrationSnapshot value);
    Task<ConfigureIntegration> EchoConfigureIntegration(ConfigureIntegration value);
    Task<ReleasedIntegration> EchoReleasedIntegration(ReleasedIntegration value);
    Task<Trace> EchoTrace(Trace value);
    Task<Owner> EchoOwner(Owner value);
    Task<BrainState> EchoBrainState(BrainState value);
    Task<SealedSecret> EchoSealedSecret(SealedSecret value);
    Task<SecretRef> EchoSecretRef(SecretRef value);
    Task<Account> EchoAccount(Account value);
    Task<Member> EchoMember(Member value);
    Task<DigitalBrain.Capabilities.Identity.BrainEntry> EchoBrainEntry(DigitalBrain.Capabilities.Identity.BrainEntry value);
    Task<BrainProvision> EchoBrainProvision(BrainProvision value);
    Task<CallDecision> EchoCallDecision(CallDecision value);
    Task<Grant> EchoGrant(Grant value);
    Task<Trace?> ReadAmbientTrace();
}

public sealed class SurrogateEchoGrain : Grain, ISurrogateEchoGrain
{
    public Task<ConnectionView> EchoConnectionView(ConnectionView value) => Task.FromResult(value);
    public Task<OAuthGrant> EchoOAuthGrant(OAuthGrant value) => Task.FromResult(value);
    public Task<ConnectionProbeResult> EchoProbeResult(ConnectionProbeResult value) => Task.FromResult(value);
    public Task<IntegrationSnapshot> EchoIntegrationSnapshot(IntegrationSnapshot value) => Task.FromResult(value);
    public Task<ConfigureIntegration> EchoConfigureIntegration(ConfigureIntegration value) => Task.FromResult(value);
    public Task<ReleasedIntegration> EchoReleasedIntegration(ReleasedIntegration value) => Task.FromResult(value);
    public Task<Trace> EchoTrace(Trace value) => Task.FromResult(value);
    public Task<Owner> EchoOwner(Owner value) => Task.FromResult(value);
    public Task<BrainState> EchoBrainState(BrainState value) => Task.FromResult(value);
    public Task<SealedSecret> EchoSealedSecret(SealedSecret value) => Task.FromResult(value);
    public Task<SecretRef> EchoSecretRef(SecretRef value) => Task.FromResult(value);
    public Task<Account> EchoAccount(Account value) => Task.FromResult(value);
    public Task<Member> EchoMember(Member value) => Task.FromResult(value);
    public Task<DigitalBrain.Capabilities.Identity.BrainEntry> EchoBrainEntry(DigitalBrain.Capabilities.Identity.BrainEntry value) => Task.FromResult(value);
    public Task<BrainProvision> EchoBrainProvision(BrainProvision value) => Task.FromResult(value);
    public Task<CallDecision> EchoCallDecision(CallDecision value) => Task.FromResult(value);
    public Task<Grant> EchoGrant(Grant value) => Task.FromResult(value);
    public Task<Trace?> ReadAmbientTrace() => Task.FromResult(TraceContext.Current);
}

public sealed class SurrogateRoundTripFacts
{
    [Fact]
    public async Task EachMovedRecordRoundTripsThroughAGrainCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(ct: ct);
        var grain = host.Services.GetRequiredService<IGrainFactory>().GetGrain<ISurrogateEchoGrain>("echo-1");

        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        var view = new ConnectionView(
            "github",
            "conn-1",
            "oauth",
            ConnectionStatus.Connected,
            "octocat",
            now.AddHours(1),
            now,
            "All good");
        Assert.Equal(view, await grain.EchoConnectionView(view));

        var grant = new OAuthGrant(
            "oauth",
            "access-token-123",
            "refresh-token-456",
            now.AddHours(2),
            "repo read:user",
            "https://github.com/octocat",
            "{\"access_token\":\"access-token-123\"}");
        Assert.Equal(grant, await grain.EchoOAuthGrant(grant));

        var probe = ConnectionProbeResult.Connected("Healthy");
        Assert.Equal(probe, await grain.EchoProbeResult(probe));

        var snapshot = new IntegrationSnapshot
        {
            IntegrationId = "github",
            Status = IntegrationStatus.Partial,
            MissingFields = ["client_secret"],
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = "abc" },
            Revision = 3,
        };
        var echoedSnapshot = await grain.EchoIntegrationSnapshot(snapshot);
        Assert.Equal(snapshot.IntegrationId, echoedSnapshot.IntegrationId);
        Assert.Equal(snapshot.Status, echoedSnapshot.Status);
        Assert.Equal(snapshot.MissingFields, echoedSnapshot.MissingFields);
        Assert.Equal(snapshot.Settings, echoedSnapshot.Settings);
        Assert.Equal(snapshot.Revision, echoedSnapshot.Revision);

        var configure = new ConfigureIntegration
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["client_secret"] = "xyz" },
        };
        var echoedConfigure = await grain.EchoConfigureIntegration(configure);
        Assert.Equal(configure.Values, echoedConfigure.Values);

        var released = new ReleasedIntegration
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = "abc", ["client_secret"] = "xyz" },
            Snapshot = snapshot,
        };
        var echoedReleased = await grain.EchoReleasedIntegration(released);
        Assert.Equal(released.Values, echoedReleased.Values);
        Assert.Equal(snapshot.IntegrationId, echoedReleased.Snapshot.IntegrationId);
        Assert.Equal(snapshot.Revision, echoedReleased.Snapshot.Revision);

        var trace = new Trace
        {
            TraceId = "trace-123",
            PayerAccountId = "acc-1",
            BrainId = "brain-1",
            Origin = new NeuronId(new BrainId("brain-1"), "edge", "request"),
            BudgetMicros = 50_000,
        };
        Assert.Equal(trace, await grain.EchoTrace(trace));

        using (TraceContext.Enter(trace))
        {
            Assert.Equal(trace, await grain.ReadAmbientTrace());
        }

        var owner = new Owner("acc-1");
        Assert.Equal(owner, await grain.EchoOwner(owner));

        var brainState = new BrainState(owner, now);
        Assert.Equal(brainState, await grain.EchoBrainState(brainState));

        var secret = new SealedSecret { Cipher = "cipher-base64", KeyId = "key-1" };
        Assert.Equal(secret, await grain.EchoSealedSecret(secret));

        var secretRef = new SecretRef { Reference = "secret://acc-1/api-key", Label = "API Key", Status = SecretStatus.Set };
        Assert.Equal(secretRef, await grain.EchoSecretRef(secretRef));

        var account = new Account { AccountId = "acc-1", Name = "Ada", OwnerPrincipalId = "ada", CreatedAt = now };
        Assert.Equal(account, await grain.EchoAccount(account));

        var member = new Member { PrincipalId = "ada", AccountId = "acc-1", BrainId = "brain-1", Role = MemberRole.Owner, DisplayName = "Ada", JoinedAt = now };
        Assert.Equal(member, await grain.EchoMember(member));

        var brainEntry = new DigitalBrain.Capabilities.Identity.BrainEntry("brain-1", BrainKind.Circle);
        Assert.Equal(brainEntry, await grain.EchoBrainEntry(brainEntry));

        var provision = new BrainProvision(member, NewBrain: true);
        Assert.Equal(provision, await grain.EchoBrainProvision(provision));

        var callDecision = new CallDecision { Allowed = false, Denial = CallDenial.MissingGrant, Explanation = "missing", ConsumesOnce = true };
        Assert.Equal(callDecision, await grain.EchoCallDecision(callDecision));

        var identityGrant = Grant.Tuple("brain:brain-1", "app:notes", "notes/*", "read", expires: now.AddDays(1), delegable: true) with
        {
            Mode = GrantMode.ThisChat,
            ConversationId = "conv-1",
            GrantedAt = now,
        };
        Assert.Equal(identityGrant, await grain.EchoGrant(identityGrant));
    }
}
