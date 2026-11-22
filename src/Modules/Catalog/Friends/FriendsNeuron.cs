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
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class FriendsRecord
{
    public PendingFriendCode[] Pending { get; set; } = [];
    public int Refused { get; set; }
    public Friend[]? Friends { get; set; }
    public int Misses { get; set; }
    public DateTimeOffset MissesSince { get; set; }
    public GroupInvite[] Groups { get; set; } = [];
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
    private IFileShares Door => Get<IFileShares>(FileSharesDefaults.Id(Id.Brain.Value));
    private IOffers Offers => Context.Offers;

    public async Task<Code> Mint()
    {
        await Owner();
        await EnsureImported();
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
        await Store(With(friends: UpsertFriend(State.Friends ?? [], added)));
        return added;
    }

    public async Task Unfriend(AccountId friend)
    {
        await Owner();
        ArgumentException.ThrowIfNullOrWhiteSpace(friend.Value);
        await EnsureImported();
        var existing = (State.Friends ?? []).FirstOrDefault(row => row.Account == friend);
        if (existing is null) { return; }

        await Store(With(friends: [.. (State.Friends ?? []).Where(row => row.Account != friend)]));
        // IFriends.IsFriend is [Interleaves] and reads the already-stored roster, breaking the Door.Share <-> DropAccount cycle.
#pragma warning disable DB0033
        await Door.DropAccount(friend.Value);
        await Offers.RevokeAll(friend.Value);
#pragma warning restore DB0033
        try { await Context.Get<IFriends>(new BindingTarget.Offered(existing.Brain, FriendsDefaults.Id(existing.Brain.Value))).Unfriended(); }
        catch (UnauthorizedAccessException refused)
        {
            OsConsoleLog.For<FriendsNeuron>().LogSwallowed(refused, "the other side refused the unfriend notice");
        }
    }

    public async Task<Friend[]> List()
    {
        RequireSameBrain();
        await EnsureImported();
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
        await Store(With(pending: remaining, refused: 0, friends: UpsertFriend(State.Friends ?? [], added)));
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
        await Store(With(friends: [.. (State.Friends ?? []).Where(row => row.Account != target)]));
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        // IFriends.IsFriend is [Interleaves] and reads the already-stored roster, breaking the Door.Share <-> DropAccount cycle.
#pragma warning disable DB0033
        await Door.DropAccount(account);
        await Offers.RevokeAll(account);
#pragma warning restore DB0033
    }

    public async Task<GroupInvite[]> Groups()
    {
        RequireSameBrain();
        await EnsureImported();
        return State.Groups;
    }

    public async Task GroupSay(GroupInvite group, string text)
    {
        ArgumentNullException.ThrowIfNull(group);
        await Owner();
        await EnsureImported();
        if (!State.Groups.Any(g => g.Brain == group.Brain && g.Conversation == group.Conversation))
        {
            throw FriendRelay.Refused();
        }
        var target = new NeuronId(group.Brain, IConversation.Type, IConversation.KeyOf(group.Conversation));
        await SiloNeurons.Get<IConversation>(services, target).Say(text);
    }

    public async Task<ConversationView> GroupRead(GroupInvite group, long? sinceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        await Owner();
        await EnsureImported();
        if (!State.Groups.Any(g => g.Brain == group.Brain && g.Conversation == group.Conversation))
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

        var others = State.Groups.Where(existing => existing.Brain != group.Brain || existing.Conversation != group.Conversation);
        await Store(With(groups: [.. others, group]));
    }

    [ReadOnlyCall]
    public async Task<BrainPublicKeys> PublicKeys(CancellationToken ct = default)
    {
        _ = ct;
        RequireSameBrain();
        return await EnsurePublicKeys();
    }

    private async Task<BrainPublicKeys> EnsurePublicKeys()
    {
        if (State.SigningKeyPkcs8 is { Length: > 0 } signingPkcs8
            && State.ExchangeKeyPkcs8 is { Length: > 0 } exchangePkcs8)
        {
            using var signing = ECDsa.Create();
            signing.ImportPkcs8PrivateKey(signingPkcs8, out _);
            using var exchange = ECDiffieHellman.Create();
            exchange.ImportPkcs8PrivateKey(exchangePkcs8, out _);
            return new BrainPublicKeys(signing.ExportSubjectPublicKeyInfo(), exchange.ExportSubjectPublicKeyInfo());
        }

        using var generated = BrainKeyPair.Generate();
        var publicKeys = generated.ExportPublicKeys();
        await Store(With(
            signingKeyPkcs8: generated.SigningKey.ExportPkcs8PrivateKey(),
            exchangeKeyPkcs8: generated.ExchangeKey.ExportPkcs8PrivateKey()));
        return publicKeys;
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

    private void RequireSameBrain()
    {
        if (Context.Caller is { } caller && caller.Brain != Id.Brain)
        {
            throw PersonActs.Refused();
        }
    }

    private void RequireIsFriendCaller(AccountId account)
    {
        if (Context.Caller is not { } caller) { return; }
        if (caller.Brain == Id.Brain || caller.Via?.Brain == Id.Brain) { return; }
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
        GroupInvite[]? groups = null,
        byte[]? signingKeyPkcs8 = null,
        byte[]? exchangeKeyPkcs8 = null)
        => new()
        {
            Pending = pending ?? State.Pending,
            Refused = refused ?? State.Refused,
            Friends = friends ?? State.Friends,
            Misses = misses ?? State.Misses,
            MissesSince = missesSince ?? State.MissesSince,
            Groups = groups ?? State.Groups,
            SigningKeyPkcs8 = signingKeyPkcs8 ?? State.SigningKeyPkcs8,
            ExchangeKeyPkcs8 = exchangeKeyPkcs8 ?? State.ExchangeKeyPkcs8,
        };
}

internal sealed class FriendsFriendCheck(INeuronProxies proxies) : IFriendCheck
{
    public Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default)
        => proxies.Get<IFriends>(FriendsDefaults.Id(owner.Value)).IsFriend(new AccountId(account));
}
