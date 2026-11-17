using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.Cloud.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Organisations.Tests;

public sealed class OrganisationFacts
{
    [Theory]
    [InlineData("cloud")]
    [InlineData("local")]
    [InlineData("local+cloud")]
    [InlineData(null)]
    public void OrganisationsLoadRegardlessOfTopology(string? topology)
        => Assert.True(IntoChat.OS.Framework.Modules.OsModule.FromDigital(typeof(OrganisationsModule)).LoadsUnder(topology));

    [Fact]
    public void TheManifestRequiresIdentity()
    {
        var manifest = ModuleManifest.Of(typeof(OrganisationsModule));
        Assert.Equal("organisations", manifest.Id);
        Assert.Contains(manifest.Requires, capability => capability == Capability.Identity.V1);
        Assert.Contains(manifest.Needs, need => need.Kind == NeedKind.Http);
    }

    [Fact]
    public async Task EnsurePersonalCreatesAnAdminAndIsIdempotent()
    {
        var desk = new Desk();
        var created = await desk.Directory("ada").EnsurePersonal(Request("ada", "Ada Berg", "ada@example.com"));
        Assert.True(created.Created);
        Assert.Equal(OrganisationRole.Admin, created.Role);
        var again = await desk.Directory("ada").EnsurePersonal(Request("ada", "Ada Berg", "ada@example.com"));
        Assert.False(again.Created);
        Assert.Equal(created.OrganisationId, again.OrganisationId);
    }

    [Fact]
    public async Task TheLastAdminCannotBeDemoted()
    {
        var desk = new Desk();
        await desk.Directory("ada").EnsurePersonal(Request("ada", "Ada Berg", "ada@example.com"));
        desk.Host.CurrentCaller = Caller("ada", "brain-ada");
        var error = await Assert.ThrowsAsync<OrganisationException>(() => desk.Organisation("brain-ada").ChangeRole("ada", OrganisationRole.Member));
        Assert.Equal("LAST_ADMIN_CANNOT_DEMOTE", error.Code);
    }

    [Fact]
    public async Task AnInviteAcceptMovesTheMember()
    {
        var desk = new Desk();
        await desk.Directory("ada").EnsurePersonal(Request("ada", "Ada Berg", "ada@example.com"));
        await desk.Directory("bob").EnsurePersonal(Request("bob", "Bob Berg", "bob@example.com"));
        desk.Identity.Members[("brain-ada", "ada")] = new Member
        {
            PrincipalId = "ada",
            AccountId = "acc-ada",
            BrainId = "brain-ada",
            Role = MemberRole.Owner,
            DisplayName = "Ada Berg",
        };
        desk.Host.CurrentCaller = Caller("ada", "brain-ada");
        var invited = await desk.Invites("brain-ada").InviteMembers(["bob@example.com"], OrganisationRole.Member);
        Assert.Equal("INVITED", invited[0].Outcome);
        Assert.NotNull(desk.Mail.LastToken);

        desk.Host.CurrentCaller = Caller("bob", "brain-bob");
        var accepted = await desk.Invites("brain-ada").Accept(desk.Mail.LastToken!);
        Assert.Equal("brain-ada", accepted.OrganisationId);
        var membership = await desk.Membership("bob").Get();
        Assert.Equal("brain-ada", membership!.OrgBrain);
        var organisation = await desk.Organisation("brain-ada").Read();
        Assert.Contains(organisation.Members, member => member.PrincipalId == "bob" && member.Role == OrganisationRole.Member);
    }

    private static PersonalOrganisationRequest Request(string principal, string name, string email) => new(
        principal, "acc-" + principal, "brain-" + principal, name, email, "en", DateTimeOffset.Parse("2026-10-29T12:00:00Z"));

    private static CapabilityCaller Caller(string principal, string brain) => new(principal, "acc-" + principal, brain);

    private sealed class Desk : INeuronProxies
    {
        private readonly Dictionary<string, object> neurons = new(StringComparer.Ordinal);
        public Host Host { get; } = new();
        public Identity Identity { get; } = new();
        public RecordingMail Mail { get; } = new();
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        private readonly OrganisationsModuleOptions options = new();
        private readonly IRecipientHasher hasher = new AddressHasher();

        public OrganisationDirectoryNeuron Directory(string principal) => GetOrCreate(
            OrgIds.Of(IOrganisationDirectory.Type, IOrganisationDirectory.Key(principal)),
            () => Create<OrganisationDirectoryNeuron>(hasher, this));

        public OrganisationNeuron Organisation(string brain) => GetOrCreate(
            OrgIds.Of(IOrganisation.Type, IOrganisation.Key(brain)),
            () => Create<OrganisationNeuron>(options, Host, this, Identity, Clock, NullLogger<OrganisationNeuron>.Instance));

        public OrgMembershipNeuron Membership(string principal) => GetOrCreate(
            OrgIds.Of(IOrgMembership.Type, IOrgMembership.Key(principal)),
            () => Create<OrgMembershipNeuron>());

        public OrganisationInvitesNeuron Invites(string brain) => GetOrCreate(
            OrgIds.Of(IOrganisationInvites.Type, IOrganisationInvites.Key(brain)),
            () => Create<OrganisationInvitesNeuron>(options, hasher, Mail, Identity, new AllowAllPersonalOrganisationGuard(), Host, this, Clock, NullLogger<OrganisationInvitesNeuron>.Instance));

        public T Get<T>(NeuronId id) where T : class => id.Type switch
        {
            IInviteToken.Type => (T)(object)GetOrCreate(id, () => Create<InviteTokenNeuron>(Clock)),
            IOrgMembership.Type => (T)(object)GetOrCreate(id, () => Create<OrgMembershipNeuron>()),
            IOrganisation.Type => (T)(object)OrganisationNeuron(id),
            IOrganisationInvites.Type => (T)(object)InvitesNeuron(id),
            IOrganisationDirectory.Type => (T)(object)GetOrCreate(id, () => Create<OrganisationDirectoryNeuron>(hasher, this)),
            _ => throw new InvalidOperationException($"No neuron {id}."),
        };

        private OrganisationNeuron OrganisationNeuron(NeuronId id) => GetOrCreate(id, () => Create<OrganisationNeuron>(options, Host, this, Identity, Clock, NullLogger<OrganisationNeuron>.Instance));

        private OrganisationInvitesNeuron InvitesNeuron(NeuronId id) =>
            GetOrCreate(id, () => Create<OrganisationInvitesNeuron>(options, hasher, Mail, Identity, new AllowAllPersonalOrganisationGuard(), Host, this, Clock, NullLogger<OrganisationInvitesNeuron>.Instance));

        private static T Create<T>(params object[] args) where T : Neuron
            => (T)Activator.CreateInstance(
                typeof(T),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                args,
                null)!;

        private T GetOrCreate<T>(NeuronId id, Func<T> create) where T : Neuron
        {
            if (neurons.TryGetValue(id.ToString(), out var existing)) { return (T)existing; }
            var neuron = create();
            NeuronAttachment.Attach(neuron, new FakeNeuronContext(id));
            NeuronAttachment.Activate(neuron, CancellationToken.None).GetAwaiter().GetResult();
            neurons[id.ToString()] = neuron;
            return neuron;
        }
    }

    private sealed class Host : IOrganisationHost
    {
        public CapabilityCaller? CurrentCaller { get; set; }
        public IDisposable StampModule() => new Noop();
        public Task InModuleCall(Func<Task> call) => call();
        public Task<T> InModuleCall<T>(Func<Task<T>> call) => call();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class Identity : IOrganisationIdentity
    {
        public Dictionary<(string Brain, string Principal), Member> Members { get; } = [];
        public Task<Member?> Membership(string brainId, string principalId, CancellationToken cancellationToken = default)
            => Task.FromResult(Members.TryGetValue((brainId, principalId), out var member) ? member : null);
        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default)
        {
            Members[(member.BrainId, member.PrincipalId)] = member;
            return Task.FromResult<Member?>(member);
        }
        public Task RemovePrincipalMember(string brainId, string principalId, CancellationToken cancellationToken = default)
        {
            Members.Remove((brainId, principalId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMail : IOrganisationMail
    {
        public string? LastToken { get; private set; }
        public Task<bool> SendTeamInvite(string email, string inviterName, string organisationName, string actionUrl, CancellationToken cancellationToken = default)
        {
            var marker = "/invite#";
            var index = actionUrl.IndexOf(marker, StringComparison.Ordinal);
            LastToken = index < 0 ? null : actionUrl[(index + marker.Length)..];
            return Task.FromResult(true);
        }
        public Task SendInviteAccepted(string email, string memberName, string organisationName, string actionUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AddressHasher : IRecipientHasher
    {
        public string Hash(string purpose, string email) => purpose + ":" + email.Trim().ToUpperInvariant();
    }
}
