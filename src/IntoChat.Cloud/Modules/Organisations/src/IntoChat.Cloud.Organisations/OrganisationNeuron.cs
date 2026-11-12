using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Organisations;

[Neuron(IOrganisation.Type)]
internal sealed class OrganisationNeuron(
    OrganisationsModuleOptions options,
    IOrganisationHost host,
    INeuronProxies proxies,
    IOrganisationIdentity identity,
    TimeProvider clock,
    ILogger<OrganisationNeuron> logger) : Neuron<OrganisationState>, IOrganisation
{
    private bool Exists => State.Version > 0;

    private string OrgBrain => Id.Brain.Value;

    public Task<OrganisationState> Get()
    {
        var current = Active();
        RequireMember(current, Caller());
        return Task.FromResult(current.Clone());
    }

    public async Task Rename(string name)
    {
        var current = Active();
        RequireAdmin(current, Caller());
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > options.MaxOrganisationNameLength) { throw new OrganisationException("NAME_INVALID"); }
        if (trimmed == current.Name) { return; }
        var next = current.Clone();
        next.Name = trimmed;
        next.Version++;
        await Save(next, new OrganisationBookkeeping(current.OrgId, "rename"));
    }

    public async Task ChangeRole(string principalId, OrganisationRole role)
    {
        var current = Active();
        RequireAdmin(current, Caller());
        if (!Enum.IsDefined(role)) { throw new OrganisationException("ROLE_INVALID"); }
        var target = current.Members.FirstOrDefault(member => member.PrincipalId == principalId) ?? throw new OrganisationException("MEMBER_NOT_FOUND");
        if (target.Role == role) { return; }
        var admins = current.Members.Count(member => member.Role == OrganisationRole.Admin);
        if (target.Role == OrganisationRole.Admin && role == OrganisationRole.Member && admins == 1)
        {
            throw new OrganisationException("LAST_ADMIN_CANNOT_DEMOTE");
        }
        var previous = target.Role;
        var next = current.Clone();
        next.Members.Single(member => member.PrincipalId == principalId).Role = role;
        next.Version++;
        await Save(next, new RoleChanged(current.OrgId, principalId, role, previous));
    }

    public async Task RemoveMember(string principalId)
    {
        var current = Active();
        var caller = Caller();
        RequireMember(current, caller);
        if (current.Status == OrganisationStatus.Dissolved) { throw new OrganisationException("ORGANISATION_DISSOLVED"); }
        if (principalId != caller.PrincipalId) { RequireAdmin(current, caller); }
        var target = current.Members.FirstOrDefault(member => member.PrincipalId == principalId) ?? throw new OrganisationException("MEMBER_NOT_FOUND");
        if (principalId == current.PersonalOwnerPrincipalId) { throw new OrganisationException("OWNER_CANNOT_BE_REMOVED"); }
        if (target.Role == OrganisationRole.Admin && current.Members.Count(member => member.Role == OrganisationRole.Admin) == 1)
        {
            throw new OrganisationException("LAST_ADMIN_CANNOT_LEAVE");
        }

        await host.InModuleCall(() => identity.RemovePrincipalMember(OrgBrain, principalId));
        var membership = MembershipOf(principalId);
        var stored = await host.InModuleCall(() => membership.Get());
        if (stored is not null && stored.OrgBrain == OrgBrain)
        {
            await host.InModuleCall(() => membership.Set(stored.PersonalOrgBrain, stored.PersonalOrgBrain, stored.EmailHash, stored.PersonalOrgBrain, OrgBrain));
            try
            {
                await host.InModuleCall(() => OrganisationOf(stored.PersonalOrgBrain).Revive(target.DisplayName, target));
            }
            catch (OrganisationException error) when (error.Code == "ORGANISATION_NOT_DISSOLVED")
            {
                logger.LogDebug(error, "Personal organisation {Brain} was still active, so it was not revived.", stored.PersonalOrgBrain);
            }
        }

        var next = current.Clone();
        next.Members = [.. next.Members.Where(member => member.PrincipalId != principalId)];
        next.Version++;
        await Save(next, new MemberRemoved(current.OrgId, principalId));
    }

    public async Task<bool> CreatePersonal(PersonalOrganisationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Exists) { return false; }
        if (request.BrainId != OrgBrain) { throw new OrganisationException("BRAIN_MISMATCH"); }
        var name = request.DisplayName.Trim();
        if (name.Length > options.MaxOrganisationNameLength) { name = name[..options.MaxOrganisationNameLength]; }
        var created = new OrganisationState
        {
            OrgId = OrgBrain,
            OrgBrain = OrgBrain,
            Name = name,
            Status = OrganisationStatus.Active,
            PersonalOwnerPrincipalId = request.PrincipalId,
            Members = [Admin(request.PrincipalId, request.DisplayName, request.Email, request.VerifiedAt)],
            Version = 1,
        };
        await Save(created, new OrganisationCreated(created.OrgId, created.Name, request.PrincipalId));
        return true;
    }

    public Task<OrganisationState> Read() => Task.FromResult(Active().Clone());

    public Task<OrganisationRole?> RoleOf(string principalId)
    {
        if (!Exists) { return Task.FromResult<OrganisationRole?>(null); }
        var member = State.Members.FirstOrDefault(candidate => candidate.PrincipalId == principalId);
        return Task.FromResult(member?.Role);
    }

    public async Task AddMember(OrganisationMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var current = Active();
        if (current.Members.Any(existing => existing.PrincipalId == member.PrincipalId)) { return; }
        var next = current.Clone();
        next.Members = [.. next.Members, member.Clone()];
        next.Version++;
        await Save(next, new OrganisationBookkeeping(current.OrgId, "add-member"));
    }

    public async Task Dissolve()
    {
        if (!Exists || State.Status == OrganisationStatus.Dissolved) { return; }
        var next = State.Clone();
        next.Status = OrganisationStatus.Dissolved;
        next.Version++;
        await Save(next, new OrganisationBookkeeping(next.OrgId, "dissolve"));
    }

    public async Task Revive(string name, OrganisationMember admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        if (!Exists) { throw new OrganisationException("ORGANISATION_NOT_FOUND"); }
        if (State.Status != OrganisationStatus.Dissolved) { throw new OrganisationException("ORGANISATION_NOT_DISSOLVED"); }
        var next = State.Clone();
        next.Status = OrganisationStatus.Active;
        next.Name = string.IsNullOrWhiteSpace(name) ? next.Name : name.Trim();
        next.Members =
        [
            new OrganisationMember
            {
                PrincipalId = admin.PrincipalId,
                DisplayName = admin.DisplayName,
                Email = admin.Email,
                Role = OrganisationRole.Admin,
                JoinedAt = clock.GetUtcNow(),
            },
        ];
        next.Version++;
        await Save(next, new OrganisationBookkeeping(next.OrgId, "revive"));
    }

    public async Task RemoveMemberRecord(string principalId)
    {
        var current = Active();
        if (current.Members.All(member => member.PrincipalId != principalId)) { return; }
        var next = current.Clone();
        next.Members = [.. next.Members.Where(member => member.PrincipalId != principalId)];
        next.Version++;
        await Save(next, new MemberRemoved(current.OrgId, principalId));
    }

    private OrganisationState Active() => Exists ? State : throw new OrganisationException("ORGANISATION_NOT_FOUND");

    private static OrganisationMember Admin(string principalId, string displayName, string email, DateTimeOffset joinedAt) => new()
    {
        PrincipalId = principalId,
        DisplayName = displayName,
        Email = email,
        Role = OrganisationRole.Admin,
        JoinedAt = joinedAt,
    };

    private CapabilityCaller Caller() => host.CurrentCaller ?? throw new UnauthorizedAccessException("The caller is not signed in.");

    private static OrganisationMember RequireMember(OrganisationState current, CapabilityCaller caller) =>
        current.Members.FirstOrDefault(member => member.PrincipalId == caller.PrincipalId)
        ?? throw new UnauthorizedAccessException("The caller is not a member of this organisation.");

    private static void RequireAdmin(OrganisationState current, CapabilityCaller caller)
    {
        if (current.Status == OrganisationStatus.Dissolved) { throw new OrganisationException("ORGANISATION_DISSOLVED"); }
        if (RequireMember(current, caller).Role != OrganisationRole.Admin)
        {
            throw new UnauthorizedAccessException("Only an administrator may change the organisation.");
        }
    }

    private IOrgMembership MembershipOf(string principalId) =>
        proxies.Get<IOrgMembership>(OrgIds.Of(IOrgMembership.Type, IOrgMembership.Key(principalId)));

    private IOrganisation OrganisationOf(string brain) =>
        proxies.Get<IOrganisation>(OrgIds.Of(IOrganisation.Type, IOrganisation.Key(brain)));
}
