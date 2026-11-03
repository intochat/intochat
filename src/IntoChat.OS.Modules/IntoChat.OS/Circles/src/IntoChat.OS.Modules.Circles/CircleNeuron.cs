using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Spi;
using DigitalBrain.State;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles.Rules;
using IntoChat.OS.Modules.Circles.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Circles;

[ExportableState]
internal sealed class CircleRecord
{
    public string Name { get; set; } = "";
    // The creator's account.
    public string Owner { get; set; } = "";
    public string[] Members { get; set; } = [];
    public string[] SharedApps { get; set; } = [];
    public int ComputeSharePercent { get; set; }
    public string[] Invited { get; set; } = [];
    public long Version { get; set; }
    // R155: each slot of a shared app bound at Share, so a sharer's removal can withdraw it.
    public CircleBinding[] Bindings { get; set; } = [];
    // D-10: members the creator made admins; owners and admins mint invite codes.
    public string[] Admins { get; set; } = [];

    public CircleRecord Clone() => (CircleRecord)MemberwiseClone();
}

// One slot of a shared app: the sharer's account, the slot, and the connection it names: the circle's own copy
// (Copy) or the sharer's connection left behind.
internal sealed record CircleBinding(string AccountId, string AppName, string Slot, string ConnectionKey);

// Members share a runtime; membership and selected connection bindings govern access.
// A plain neuron (06-08) at "{circle}/circle". A sharer's own brain is reached through IBrainCalls, as that member acting there.
[DigitalBrain.Neuron(CircleGrainType.Name)]
internal sealed class CircleNeuron(
    ICapabilityBroker broker,
    IServiceProvider plain,
    ILogger<CircleNeuron> logger)
    : DigitalBrain.Neuron<CircleRecord>, ICircle, ICircleStateReader
{
    private string CircleBrain => Id.Brain.Value;

    private CircleRecord Snapshot => base.State;

    // A founded circle has its creator; a deleted one is saved empty.
    private bool Exists => base.State.Owner.Length > 0;

    private IServiceProvider ServiceProvider => Context.Services;

    // Revocations committed by the next save, in order (a plain signal is published by the save that commits it).
    private readonly List<DigitalBrain.Signal> revoked = [];

    public Task<string[]> Members() => Task.FromResult(Core().Members.ToArray());

    // ICircle.State names the circle's view; the persisted record is the base neuron's State.
    public new Task<CircleState> State() => Task.FromResult(Describe(Core()));

    public Task BindPublicationButton()
    {
        MemberChange();
        return SiloNeurons.Get<IntoChat.OS.Modules.Flutter.IUiBinding>(plain, IApp.Key(CircleBrain, "circleroom") + "/bring-button")
            .Bind(IntoChat.OS.Modules.Flutter.UiHandlers.IdOf(Id));
    }

    public Task HandleUiEvent(IntoChat.OS.Modules.Flutter.UiEvent uiEvent)
    {
        MemberChange();
        return PublicationButton.Handle(plain, CircleBrain, "circleroom", "bring-button", uiEvent, BringPublication);
    }

    public async Task Found(string name)
    {
        await Check("Found", new CircleNameIsValid(name));
        if (Exists) { return; }
        var creator = Caller().AccountId;
        // Inside the brain's Create turn: the compute share (which reads the brain) is stated on the first change.
        var founded = new CircleRecord { Name = name, Owner = creator, Members = [creator], Version = 1 };
        await Save(founded, Changed(founded));
    }

    public async Task Invite(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        RequirePerson();
        var next = MemberChange();
        await Check("Invite", new AccountIsNotYetInvitedOrMember(next.Name, accountId, next.Members.Contains(accountId) || next.Invited.Contains(accountId)));
        next.Invited = [.. next.Invited, accountId];
        await CommitCore(next);
    }

    public async Task SetAdmin(string accountId, bool admin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        RequirePerson();
        var next = MemberChange();
        if (Caller().AccountId != next.Owner) { throw new UnauthorizedAccessException($"Only {next.Name}'s creator names its admins."); }
        await Check("SetAdmin", new AccountIsCircleMember(next.Name, accountId, next.Members.Contains(accountId)));
        next.Admins = admin ? [.. next.Admins.Where(known => known != accountId), accountId] : [.. next.Admins.Where(known => known != accountId)];
        await CommitCore(next);
    }

    public Task<string> RoleOf(string accountId)
    {
        var record = Core();
        return Task.FromResult(accountId == record.Owner ? "owner"
            : !record.Members.Contains(accountId) ? ""
            : record.Admins.Contains(accountId) ? "admin" : "member");
    }

    public Task<string> Name() => Task.FromResult(Core().Name);

    // R129: the invitation is the authority for a membership; the caller's machine joins the circle with it.
    public async Task<CircleState> Accept()
    {
        RequirePerson();
        var caller = Caller();
        var record = Core();
        await Check("Accept", new CallerHasCircleInvitation(record.Name, caller.AccountId, caller.BrainId == CircleBrain && record.Invited.Contains(caller.AccountId)));
        await (await broker.Identity()).EnsureMember(new Member
        {
            PrincipalId = caller.PersonId,
            AccountId = caller.AccountId,
            BrainId = CircleBrain,
            Role = MemberRole.Member,
            DisplayName = caller.PersonId,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        var next = record.Clone();
        next.Invited = [.. next.Invited.Where(invited => invited != caller.AccountId)];
        next.Members = [.. next.Members, caller.AccountId];
        return await CommitCore(next);
    }

    // R129: the creator removes anyone; a member removes itself. The creator leaving deletes the circle.
    public async Task Remove(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var caller = Caller();
        var next = MemberChange();
        if (caller.AccountId != next.Owner && caller.AccountId != accountId)
        { throw new UnauthorizedAccessException($"Only {next.Name}'s creator removes another member."); }
        if (accountId == next.Owner)
        {
            await Check("Remove", new CircleCreatorCannotBeRemoved(next.Name, caller.AccountId == next.Owner));
            await Delete(next);
            return;
        }
        await Check("Remove", new AccountIsCircleMember(next.Name, accountId, next.Members.Contains(accountId) || next.Invited.Contains(accountId)));
        await Drop(next, accountId);
        await CommitCore(next);
    }

    private async Task Drop(CircleRecord next, string accountId)
    {
        if (next.Members.Contains(accountId))
        {
            await (await broker.Identity()).RemoveMember(CircleBrain, accountId);
            revoked.Add(new CircleMembershipRevoked(accountId, next.Members.ToArray()));
        }
        next.Members = [.. next.Members.Where(member => member != accountId)];
        next.Invited = [.. next.Invited.Where(invited => invited != accountId)];
        await Unbind(next, accountId);
    }

    // R155: every binding of the removed account's connections into the circle goes: the sharing record on its own
    // connection, revoking the issued reference grants on the owner brain, and unbinding the shared app's slot.
    private async Task Unbind(CircleRecord next, string accountId)
    {
        var memberBindings = next.Bindings.Where(binding => binding.AccountId == accountId).ToArray();
        var appsToUnshare = memberBindings.Select(b => b.AppName).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var appSources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var binding in memberBindings)
        {
            try
            {
                var source = ExtractBrain(binding.ConnectionKey);
                appSources[binding.AppName] = source;

                if (!string.IsNullOrEmpty(binding.Slot))
                {
                    var grantsNeuron = SiloNeurons.Get<IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron>(plain, IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron.Key(source));
                    await BrainActs.In(broker, source, async () =>
                    {
                        await grantsNeuron.Revoke($"circle:{CircleBrain}:conn:{binding.ConnectionKey}");
                    });
                    var app = IntoChat.OS.Modules.Apps.IApp.Key(CircleBrain, binding.AppName);
                    await plain.GetConnection(binding.ConnectionKey).UnshareFrom(CircleBrain, app);
                }
            }
            // The gate also requires the sharer to be a member (R155), so a binding not cleared here admits nothing.
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Circle {Circle} could not clear the binding of {Connection} on {App}.{Slot}.", CircleBrain, binding.ConnectionKey, binding.AppName, binding.Slot);
            }
        }

        foreach (var (appName, source) in appSources)
        {
            try
            {
                var grantsNeuron = SiloNeurons.Get<IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron>(plain, IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron.Key(source));
                await BrainActs.In(broker, source, async () =>
                {
                    await grantsNeuron.Revoke($"circle:{CircleBrain}:app:{appName}");
                });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Circle {Circle} could not revoke app reference grant for {App} on {Source}.", CircleBrain, appName, source);
            }
        }

        next.SharedApps = [.. next.SharedApps.Where(app => !appsToUnshare.Contains(app))];
        next.Bindings = [.. next.Bindings.Where(binding => binding.AccountId != accountId)];
        next.Admins = [.. next.Admins.Where(admin => admin != accountId)];
    }

    // The creator leaving revokes other memberships and clears the circle.
    private async Task Delete(CircleRecord next)
    {
        foreach (var member in next.Members.Concat(next.Invited).Where(member => member != next.Owner).Distinct().ToArray()) { await Drop(next, member); }
        await Unbind(next, next.Owner);
        // The circle is cleared: an empty record, so Exists is false and a later Found could found it again.
        await Save(new CircleRecord(), [.. Drain(), new CircleChanged(next.Name, [], [], 0, Snapshot.Members)]);
    }

    private CircleRecord Core() => Exists ? Snapshot : throw new KeyNotFoundException($"Circle {CircleBrain} has not been founded.");

    // A change to the circle core is a member's.
    private CircleRecord MemberChange()
    {
        var record = Core();
        var caller = Caller();
        if (caller.BrainId != CircleBrain || !record.Members.Contains(caller.AccountId))
        { throw new UnauthorizedAccessException($"{caller.AccountId} is not a member of {record.Name}."); }
        return record.Clone();
    }

    private async Task<CircleState> CommitCore(CircleRecord next)
    {
        next.Version++;
        await Save(next, [.. Drain(), Changed(next) with { PreviousMembers = Snapshot.Members }]);
        return Describe(next);
    }

    private DigitalBrain.Signal[] Drain()
    {
        DigitalBrain.Signal[] pending = [.. revoked];
        revoked.Clear();
        return pending;
    }

    // R126: inviting and accepting are a person's, on the /brains/{circle} route; an app's script is refused them.
    private static void RequirePerson()
        => CallerAdmission.RequirePerson("Inviting to a circle and accepting an invitation are a person's own; a script may not.");

    private static CapabilityCaller Caller()
        => CallerContextStamper.TryGet(out var caller) ? caller : throw new UnauthorizedAccessException("A circle acts only on a stamped caller.");

    public async Task Share(string appName, string[] connectionIds) => await ShareIntoCircle(appName, connectionIds ?? []);

    public async Task<AppSnapshot> BringPublication(AppPublicationRef publication, string targetName, Guid operationId)
    {
        var next = MemberChange();
        var caller = Caller();
        try
        {
            var installed = await ServiceProvider.GetRequiredService<AppInstaller>().InstallExact(ServiceProvider.GetRequiredService<INeuronProxies>(), publication, targetName, operationId);
            await IndexInstallation(installed);
            return installed;
        }
        catch
        {
            // Import persists its receipt before deployment; a failed deploy is still an installed copy.
            if (!string.IsNullOrWhiteSpace(targetName) && targetName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            { await IndexInstallation(await SiloNeurons.Get<IntoChat.OS.Modules.Apps.IApp>(plain, IApp.Key(CircleBrain, targetName)).Read()); }
            throw;
        }

        async Task IndexInstallation(AppSnapshot app)
        {
            if (app.Installation != new AppInstallReceipt(operationId, publication, targetName, caller.AccountId)
                || next.SharedApps.Contains(targetName, StringComparer.Ordinal)) { return; }
            next.SharedApps = [.. next.SharedApps, targetName];
            await CommitCore(next);
        }
    }

    private async Task ShareIntoCircle(string appName, string[] connectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var next = MemberChange();
        var caller = Caller();
        var source = await SharerBrain(caller, appName);
        var app = SiloNeurons.Get<IntoChat.OS.Modules.Apps.IApp>(plain, IntoChat.OS.Modules.Apps.IApp.Key(source, appName));
        var shared = await BrainActs.In(broker, source, app.Read);

        var grantsNeuron = SiloNeurons.Get<IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron>(plain, IntoChat.OS.Authority.Identity.Grants.IGrantsNeuron.Key(source));
        await BrainActs.In(broker, source, async () =>
        {
            await grantsNeuron.Grant(IntoChat.OS.Connections.Identity.Grant.Tuple(
                $"brain:{source}",
                $"brain:{CircleBrain}",
                IntoChat.OS.Modules.Apps.IApp.Key(source, appName),
                IntoChat.OS.Connections.Identity.GrantRelations.Run) with
            {
                Id = $"circle:{CircleBrain}:app:{appName}",
            });
        });

        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        List<CircleBinding> bindings = [];
        foreach (var slot in shared.Manifest.Slots)
        {
            if (!shared.Connections.TryGetValue(slot.Name, out var id) || id.Length == 0) { continue; }
            await Check("Share", new SharedSlotIsConnectorBound(slot.Name, slot.Capability));
            if (connectionIds.Length > 0 && !connectionIds.Contains(id, StringComparer.Ordinal)) { continue; }
            var owned = ConnectionKeys.For(source, slot.Connector, id);
            await BrainActs.In(broker, source, async () =>
            {
                await grantsNeuron.Grant(IntoChat.OS.Connections.Identity.Grant.Tuple(
                    $"brain:{source}",
                    $"brain:{CircleBrain}",
                    owned,
                    $"use-via:{appName}") with
                {
                    Id = $"circle:{CircleBrain}:conn:{owned}",
                });
            });
            slots[slot.Name] = owned;
            bindings.Add(new(caller.AccountId, appName, slot.Name, owned));
        }
        if (bindings.Count == 0)
        {
            bindings.Add(new(caller.AccountId, appName, string.Empty, $"{source}/apps/{appName}"));
        }
        if (!next.SharedApps.Contains(appName)) { next.SharedApps = [.. next.SharedApps, appName]; }
        next.Bindings = [.. next.Bindings.Where(known => known.AppName != appName), .. bindings];
        await CommitCore(next);
    }

    private static string ExtractBrain(string key)
    {
        var clean = key.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? key[ConnectionKeys.NeuronScheme.Length..]
            : key;
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? clean[..slash] : "local";
    }

    // R148: the sharer's personal brain that has the app; none or several is refused.
    private async Task<string> SharerBrain(CapabilityCaller caller, string appName)
    {
        List<string> holding = [];
        foreach (var brain in (await (await broker.Identity()).BrainsOf(caller.PersonId)).Where(entry => entry.Kind == BrainKind.Personal))
        {
            var installed = await BrainActs.In(broker, brain.Id, async () =>
                (await SiloNeurons.Get<IntoChat.OS.Modules.Apps.IApp>(plain, IntoChat.OS.Modules.Apps.IApp.Key(brain.Id, appName)).Read()).Manifest.Title.Length > 0);
            if (installed) { holding.Add(brain.Id); }
        }
        await Check("Share", new SharerHasAppToShare(caller.AccountId, appName, holding.Count, string.Join(", ", holding)));
        return holding[0];
    }

    public async Task SetComputeShare(int percent)
    {
        await Check("SetComputeShare", new ComputeSharePercentIsInRange(percent));
        var core = MemberChange();
        if (Caller().AccountId != core.Owner) { throw new UnauthorizedAccessException($"Only {core.Name}'s creator, who pays, sets its compute share."); }
        core.ComputeSharePercent = percent;
        await CommitCore(core);
    }

    private static CircleChanged Changed(CircleRecord record)
        => new(record.Name, record.Members, record.SharedApps, record.ComputeSharePercent);

    private static CircleState Describe(CircleRecord record) => new(record.Name, record.Owner, record.Members,
        record.Invited, record.SharedApps, record.ComputeSharePercent, record.Version);
}
