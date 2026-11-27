using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.State;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Modules.Identity.Contracts.Keys;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class FriendsRecord
{
    public PendingFriendCode[] Pending { get; set; } = [];
    public int Refused { get; set; }
    public Friend[]? Friends { get; set; }
    public int Misses { get; set; }
    public DateTimeOffset MissesSince { get; set; }

    // Where the invitations to friends' conversations were kept before they moved to the rooms index (IRooms). Nothing
    // adds to it any more: a roster that still holds some hands them over on its next use and stores an empty list (OWED.md).
    public GroupInvite[] Groups { get; set; } = [];

    // Where the brain's private keys were kept before they moved to the identity module (IBrainKeys). Nothing writes them any
    // more: a roster that still holds them hands them over on its next use and stores null here (OWED.md).
    public byte[]? SigningKeyPkcs8 { get; set; }
    public byte[]? ExchangeKeyPkcs8 { get; set; }
}

// Only the hash of a code is kept.
internal sealed record PendingFriendCode(string Hash, DateTimeOffset ExpiresAt);

// A brain's friend roster and single-use code door, at "{brain}/friends" (stored neuron type "friends.links").
[Neuron(FriendsDefaults.TypeName)]
internal sealed class FriendsNeuron(IServiceProvider services) : Neuron<FriendsRecord>, IFriends
{
    private const int KeptCodes = 5;

    private IBrainOwners Owners => services.GetRequiredService<IBrainOwners>();
    private ICodeBook Book => services.GetRequiredService<ICodeBook>();
    private IProfile Profile => Get<IProfile>(new NeuronId(Id.Brain, "os.profile", "profile"));
    private IBrainKeys Keys => Context.Get<IBrainKeys>(IBrainKeys.Id(Id.Brain));
    private IRooms Rooms => Context.Get<IRooms>(new NeuronId(Id.Brain, "social.rooms", IRooms.DefaultKey));

    // A host that carries the roster without the rooms index keeps the invitations in the roster, as before.
    private bool RoomsHosted => Context.ClassOf("social.rooms") is not null;
    private IOffers Offers => Context.Offers;

    public async Task<Code> Mint()
    {
        await Owner();
        await EnsureImported();
        await MoveGroupsToRooms();
        await EnsurePublicKeys();
        var now = Context.Time.GetUtcNow();
        var code = Code.New();
        var expires = now + Code.Validity;
        var hash = await Book.Bind(code, CodePurpose.Friend, Id.Brain, "", expires);
        var live = State.Pending.Where(existing => existing.ExpiresAt > now).TakeLast(KeptCodes - 1);
        await Store(With(pending: [.. live, new PendingFriendCode(hash, expires)]));
        return code;
    }

    public async Task<Friend> Redeem(Code code)
    {
        var owner = await Owner();
        await EnsureImported();
        await MoveGroupsToRooms();
        var now = Context.Time.GetUtcNow();
        var misses = State.MissesSince != default && now - State.MissesSince < Code.Validity ? State.Misses : 0;
        var missesSince = misses == 0 ? now : State.MissesSince;
        if (misses >= Code.MaxMisses) { throw PersonActs.Refused(); }

        if (!Code.TryParse(code.Value, out var normalized))
        {
            await Store(With(misses: misses + 1, missesSince: missesSince));
            throw PersonActs.Refused();
        }

        var hash = await Book.HashOf(normalized);
        var target = await Book.Find(CodePurpose.Friend, hash);
        if (target is null || target.Brain == Id.Brain)
        {
            await Store(With(misses: misses + 1, missesSince: missesSince));
            throw PersonActs.Refused();
        }

        var me = await Profile.Read();
        var myName = !string.IsNullOrWhiteSpace(me.DisplayName) ? me.DisplayName
            : !string.IsNullOrWhiteSpace(me.Handle) ? me.Handle : owner;
        var myPublicKeys = await EnsurePublicKeys();

        Friend theirs;
        try
        {
            theirs = await Context.Get<IFriends>(new BindingTarget.Offered(target.Brain, FriendsDefaults.Id(target.Brain.Value))).Accepted(hash, myName, myPublicKeys);
        }
        catch (UnauthorizedAccessException)
        {
            await Store(With(misses: misses + 1, missesSince: missesSince));
            throw;
        }

        var account = await Owners.OwnerAccount(target.Brain) ?? throw PersonActs.Refused();
        var name = !string.IsNullOrWhiteSpace(theirs.Name) ? theirs.Name : account;
        var added = new Friend(new AccountId(account), target.Brain, name, now, theirs.PublicKeys);
        await Save(With(friends: UpsertFriend(State.Friends ?? [], added)), new FriendAdded(added.Account));
        return added;
    }

    public async Task Unfriend(AccountId friend)
    {
        await Owner();
        ArgumentException.ThrowIfNullOrWhiteSpace(friend.Value);
        await EnsureImported();
        await MoveGroupsToRooms();
        var existing = (State.Friends ?? []).FirstOrDefault(row => row.Account == friend);
        if (existing is null) { return; }

        // The roster states the fact and revokes the brain's offers; what a neuron kept for the friend (the file door's
        // shares) that neuron drops when it hears the fact, and it asks IsFriend on every call until then.
        await Save(With(friends: [.. (State.Friends ?? []).Where(row => row.Account != friend)]), new FriendRemoved(friend));
        await Offers.RevokeAll(friend.Value);
        try { await Context.Get<IFriends>(new BindingTarget.Offered(existing.Brain, FriendsDefaults.Id(existing.Brain.Value))).Unfriended(); }
        catch (UnauthorizedAccessException refused)
        {
            OsConsoleLog.For<FriendsNeuron>().LogSwallowed(refused, "the other side refused the unfriend notice");
        }
    }

    public async Task<Friend[]> List()
    {
        RequireSameBrain(PersonActs.Refused);
        await EnsureImported();
        await MoveGroupsToRooms();
        return State.Friends ?? [];
    }

    public async Task<bool> IsFriend(AccountId account)
    {
        RequireIsFriendCaller(account);
        await EnsureImported();
        return (State.Friends ?? []).Any(friend => friend.Account == account);
    }

    public async Task<Friend> Accepted(string codeHash, string name, BrainPublicKeys? publicKeys = null)
    {
        var (account, brain) = await FriendRelay.Require(Context.Caller, FriendsDefaults.TypeName, Owners);
        if (brain == Id.Brain) { throw PersonActs.Refused(); }
        await EnsureImported();

        var now = Context.Time.GetUtcNow();
        var match = State.Pending.FirstOrDefault(pending => pending.ExpiresAt > now && Code.HashesEqual(pending.Hash, codeHash));
        if (match is null)
        {
            var refused = State.Refused + 1;
            await Store(refused >= Code.MaxMisses
                ? With(pending: [], refused: 0)
                : With(refused: refused));
            throw PersonActs.Refused();
        }

        var myPublicKeys = await EnsurePublicKeys();
        var remaining = State.Pending.Where(pending => pending != match && pending.ExpiresAt > now).ToArray();
        var redeemerName = !string.IsNullOrWhiteSpace(name) ? name : account;
        var added = new Friend(new AccountId(account), brain, redeemerName, now, publicKeys);
        // The caller is a person of another brain. The fact is published as this brain's own module: the silo refuses a
        // nudge to a subscriber of this brain that carries a caller of another brain, and drops it without a word.
        using (PersonActs.AsFilesModule(services, Id.Brain))
        {
            await Save(With(pending: remaining, refused: 0, friends: UpsertFriend(State.Friends ?? [], added)), new FriendAdded(added.Account));
        }
        await Book.Spend(match.Hash);

        string myName;
        using (PersonActs.AsFilesModule(services, Id.Brain))
        {
            var mine = await Profile.Read();
            myName = !string.IsNullOrWhiteSpace(mine.DisplayName) ? mine.DisplayName
                : !string.IsNullOrWhiteSpace(mine.Handle) ? mine.Handle : Id.Brain.Value;
        }
        var myAccount = await Owners.OwnerAccount(Id.Brain) ?? Id.Brain.Value;
        return new Friend(new AccountId(myAccount), Id.Brain, myName, now, myPublicKeys);
    }

    public async Task Unfriended()
    {
        var (account, brain) = await FriendRelay.Require(Context.Caller, FriendsDefaults.TypeName, Owners);
        if (brain == Id.Brain) { throw PersonActs.Refused(); }
        await EnsureImported();

        var target = new AccountId(account);
        // As this brain's own module, for the offers and for the fact (see Accepted).
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        if ((State.Friends ?? []).Any(row => row.Account == target))
        {
            await Save(With(friends: [.. (State.Friends ?? []).Where(row => row.Account != target)]), new FriendRemoved(target));
        }
        await Offers.RevokeAll(account);
    }

    public async Task<GroupInvite[]> Groups()
    {
        RequireSameBrain(PersonActs.Refused);
        await EnsureImported();
        return await GroupsNow();
    }

    public async Task GroupSay(GroupInvite group, string text)
    {
        ArgumentNullException.ThrowIfNull(group);
        RequireSameBrain(PersonActs.Refused);
        await EnsureImported();
        if (!(await GroupsNow()).Any(g => g.Brain == group.Brain && g.Conversation == group.Conversation))
        {
            throw FriendRelay.Refused();
        }
        var target = new NeuronId(group.Brain, IConversation.Type, IConversation.KeyOf(group.Conversation));
        await SiloNeurons.Get<IConversation>(services, target).Say(text);
    }

    public async Task<ConversationView> GroupRead(GroupInvite group, long? sinceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        RequireSameBrain(PersonActs.Refused);
        await EnsureImported();
        if (!(await GroupsNow()).Any(g => g.Brain == group.Brain && g.Conversation == group.Conversation))
        {
            throw FriendRelay.Refused();
        }
        var target = new NeuronId(group.Brain, IConversation.Type, IConversation.KeyOf(group.Conversation));
        return await SiloNeurons.Get<IConversation>(services, target).Read(sinceRevision);
    }

    public async Task Invited(GroupInvite group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var (account, brain) = await FriendRelay.Require(Context.Caller, IConversation.Type, Owners);
        if (brain == Id.Brain || group.Brain != brain || !string.Equals(group.By.Value, account, StringComparison.Ordinal))
        {
            throw FriendRelay.Refused();
        }
        await EnsureImported();
        var inviter = new AccountId(account);
        if (!(State.Friends ?? []).Any(friend => friend.Account == inviter))
        {
            throw FriendRelay.Refused();
        }

        if (!RoomsHosted)
        {
            var others = State.Groups.Where(existing => existing.Brain != group.Brain || existing.Conversation != group.Conversation);
            await Store(With(groups: [.. others, group]));
            return;
        }

        // The rooms index keeps the invitation; the roster only hands it on, as this brain's own module (the caller is a
        // person of another brain).
        await MoveGroupsToRooms();
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        await Rooms.InvitedToConversation(ToInvite(group));
    }

    private static ConversationInvite ToInvite(GroupInvite group) => new(group.Brain, group.Conversation, group.Title, group.By);

    private static bool SameGroup(GroupInvite one, ConversationInvite other) => one.Brain == other.Brain && one.Conversation == other.Conversation;

    // The invitations this person holds: those of the rooms index, and any the roster has not handed over yet, each once.
    private async Task<GroupInvite[]> GroupsNow()
    {
        if (!RoomsHosted) { return State.Groups; }
        await MoveGroupsToRooms();
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        var kept = await Rooms.Conversations();
        return
        [
            .. kept.Select(static invite => new GroupInvite(invite.Brain, invite.Conversation, invite.Title, invite.By)),
            .. State.Groups.Where(group => !kept.Any(invite => SameGroup(group, invite))),
        ];
    }

    // A roster written by an earlier version still holds the invitations to friends' conversations. They are moved once, in
    // two steps that may be interrupted between them: first the rooms index adopts them (one it already holds is left as it
    // is, so a second attempt changes nothing) and what it now holds is read back; only when every one is there is the
    // roster stored with an empty list. After an interruption both hold them, readers are answered from the rooms index,
    // and the next call finishes. If the rooms index cannot be reached, the roster keeps its list and tries on a later call.
    // Not run from IsFriend, Accepted or Unfriended: the rooms index asks IsFriend while it is in a call of its own.
    private async Task MoveGroupsToRooms()
    {
        if (State.Groups.Length == 0 || !RoomsHosted) { return; }
        try
        {
            using var module = PersonActs.AsFilesModule(services, Id.Brain);
            await Rooms.AdoptConversations([.. State.Groups.Select(ToInvite)]);
            var held = await Rooms.Conversations();
            if (!State.Groups.All(group => held.Any(invite => SameGroup(group, invite))))
            {
                OsConsoleLog.For<FriendsNeuron>().LogWarning("The rooms index of {Brain} does not hold every invitation the roster handed over; the roster keeps its list.", Id.Brain.Value);
                return;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<FriendsNeuron>().LogSwallowed(error, "the roster could not hand its conversation invitations to the rooms index; it keeps them and tries on a later call");
            return;
        }

        await Store(With(groups: []));
    }

    [ReadOnlyCall]
    public async Task<BrainPublicKeys> PublicKeys(CancellationToken ct = default)
    {
        _ = ct;
        RequireSameBrain(PersonActs.Refused);
        return await EnsurePublicKeys();
    }

    // The brain's key pair lives in the identity module, sealed (IBrainKeys); the roster keeps no private key. The keys are
    // asked for as this brain's own module: the call may run for a person of another brain (Accepted).
    private async Task<BrainPublicKeys> EnsurePublicKeys()
    {
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        if (State.SigningKeyPkcs8 is { Length: > 0 } signingPkcs8
            && State.ExchangeKeyPkcs8 is { Length: > 0 } exchangePkcs8)
        {
            return await MoveKeysToIdentity(signingPkcs8, exchangePkcs8);
        }

        return await Keys.Ensure();
    }

    // A roster written by an earlier version still holds the pair. It is moved once, in two steps that may be interrupted
    // between them: first the identity neuron adopts the pair (only while it holds none, so a second attempt changes
    // nothing) and what it now holds is read back; only when that is this very pair is the roster stored without it. After
    // an interruption both hold the same pair, and the next call finds that and finishes. If the identity neuron holds
    // another pair, that one is the brain's: the roster is left as it is and nothing is overwritten.
    private async Task<BrainPublicKeys> MoveKeysToIdentity(byte[] signingPkcs8, byte[] exchangePkcs8)
    {
        using var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(signingPkcs8, out _);
        using var exchange = ECDiffieHellman.Create();
        exchange.ImportPkcs8PrivateKey(exchangePkcs8, out _);
        var mine = new BrainPublicKeys(signing.ExportSubjectPublicKeyInfo(), exchange.ExportSubjectPublicKeyInfo());

        await Keys.Adopt(signingPkcs8, exchangePkcs8);
        var held = await Keys.PublicKeys() ?? throw new InvalidOperationException("The brain's key pair was not kept; the roster still holds it.");
        if (!held.SigningKey.AsSpan().SequenceEqual(mine.SigningKey) || !held.ExchangeKey.AsSpan().SequenceEqual(mine.ExchangeKey))
        {
            OsConsoleLog.For<FriendsNeuron>().LogWarning("The roster of {Brain} holds a key pair that is not the brain's; it is left in the roster record and not used.", Id.Brain.Value);
            return held;
        }

        await Store(WithoutPrivateKeys());
        return held;
    }

    private async Task EnsureImported()
    {
        if (State.Friends is not null) { return; }
        ProfileState profile;
        using (PersonActs.AsFilesModule(services, Id.Brain))
        {
            profile = await Profile.Read();
        }
        var imported = (profile.Friends ?? [])
            .Where(static f => f.Confirmed && !string.IsNullOrWhiteSpace(f.Account) && !string.IsNullOrWhiteSpace(f.BrainId))
            .GroupBy(static f => f.Account, StringComparer.Ordinal)
            .Select(static g => g.First())
            .Select(static f => new Friend(
                new AccountId(f.Account),
                new BrainId(f.BrainId),
                !string.IsNullOrWhiteSpace(f.DisplayName) ? f.DisplayName : f.Handle,
                f.AddedAt))
            .ToArray();
        await Store(With(friends: imported));
    }

    private async Task<string> Owner()
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        return brain == Id.Brain ? account : throw PersonActs.Refused();
    }

    private void RequireIsFriendCaller(AccountId account)
    {
        var caller = RequireCaller();
        if (IsSameBrainCaller() || caller.Via?.Brain == Id.Brain) { return; }
        if (caller.Actor is Actor.Person person && string.Equals(person.Account, account.Value, StringComparison.Ordinal)) { return; }
        throw PersonActs.Refused();
    }

    private static Friend[] UpsertFriend(Friend[] existing, Friend added)
        => [.. existing.Where(row => row.Account != added.Account), added];

    private FriendsRecord With(
        PendingFriendCode[]? pending = null,
        int? refused = null,
        Friend[]? friends = null,
        int? misses = null,
        DateTimeOffset? missesSince = null,
        GroupInvite[]? groups = null)
        => new()
        {
            Pending = pending ?? State.Pending,
            Refused = refused ?? State.Refused,
            Friends = friends ?? State.Friends,
            Misses = misses ?? State.Misses,
            MissesSince = missesSince ?? State.MissesSince,
            Groups = groups ?? State.Groups,
            // Carried while a roster of an earlier version still holds them; only WithoutPrivateKeys drops them.
            SigningKeyPkcs8 = State.SigningKeyPkcs8,
            ExchangeKeyPkcs8 = State.ExchangeKeyPkcs8,
        };

    private FriendsRecord WithoutPrivateKeys()
        => new()
        {
            Pending = State.Pending,
            Refused = State.Refused,
            Friends = State.Friends,
            Misses = State.Misses,
            MissesSince = State.MissesSince,
            Groups = State.Groups,
        };
}

internal sealed class FriendsFriendCheck(INeuronProxies proxies) : IFriendCheck
{
    public Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default)
        => proxies.Get<IFriends>(FriendsDefaults.Id(owner.Value)).IsFriend(new AccountId(account));
}
