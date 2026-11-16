using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework.Logging;
using DigitalBrain.State;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class FriendLinksRecord
{
    public PendingFriendCode[] Pending { get; set; } = [];
    public int Refused { get; set; }
}

// Only the hash of a code is kept.
internal sealed record PendingFriendCode(string Hash, DateTimeOffset ExpiresAt);

// A brain's friend links, at "{brain}/friends".
[Neuron(FriendLinksDefaults.TypeName)]
internal sealed class FriendLinksNeuron(IServiceProvider services) : Neuron<FriendLinksRecord>, IFriendLinks
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int KeptCodes = 5;
    // After this many wrong codes every pending code is withdrawn: guessing gets no second day.
    private const int RefusalsBeforeWithdrawal = 20;
    private static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    private IBrainOwners Owners => services.GetRequiredService<IBrainOwners>();
    private IProfile Profile => Get<IProfile>(new DigitalBrain.NeuronId(Id.Brain, "os.profile", "profile"));
    private IFileShares Door => Get<IFileShares>(FileSharesDefaults.Id(Id.Brain.Value));

    public async Task<FriendCode> Mint()
    {
        await Owner();
        var now = Context.Time.GetUtcNow();
        var secret = new string(RandomNumberGenerator.GetItems<char>(Alphabet, 12));
        var expires = now + Validity;
        var live = State.Pending.Where(code => code.ExpiresAt > now).TakeLast(KeptCodes - 1);
        await Store(new FriendLinksRecord { Pending = [.. live, new PendingFriendCode(Hash(secret), expires)], Refused = State.Refused });
        return new FriendCode($"{Id.Brain.Value}.{secret[..4]}-{secret[4..8]}-{secret[8..]}", expires);
    }

    public async Task<ProfileFriend> Redeem(string code)
    {
        await Owner();
        var (brain, secret) = Parse(code);
        if (brain == Id.Brain.Value) { throw new ArgumentException("That is your own code.", nameof(code)); }

        var me = await Profile.Read();
        // The other side answers with its own introduction, but who it is comes from the code and from who owns that brain.
        var theirs = await SiloNeurons.Get<IFriendLinks>(services, FriendLinksDefaults.Id(brain)).Accepted(Hash(secret), me.Handle, me.DisplayName);
        var account = await Owners.OwnerAccount(new DigitalBrain.BrainId(brain)) ?? throw PersonActs.Refused();
        var profile = await Profile.ConfirmFriend(account, brain, theirs.Handle, theirs.DisplayName);
        return (profile.Friends ?? []).First(friend => friend.Account == account);
    }

    public async Task<FriendIntroduction> Accepted(string codeHash, string handle, string displayName)
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        if (brain == Id.Brain) { throw PersonActs.Refused(); }

        var now = Context.Time.GetUtcNow();
        var presented = Encoding.ASCII.GetBytes(codeHash ?? string.Empty);
        var match = State.Pending.FirstOrDefault(code => code.ExpiresAt > now
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code.Hash), presented));
        if (match is null)
        {
            var refused = State.Refused + 1;
            await Store(refused >= RefusalsBeforeWithdrawal
                ? new FriendLinksRecord()
                : new FriendLinksRecord { Pending = State.Pending, Refused = refused });
            throw PersonActs.Refused();
        }

        await Store(new FriendLinksRecord { Pending = [.. State.Pending.Where(code => code != match)] });
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        var mine = await Profile.ConfirmFriend(account, brain.Value, handle, displayName);
        var owner = await Owners.OwnerAccount(Id.Brain) ?? string.Empty;
        return new FriendIntroduction(owner, Id.Brain.Value, mine.Handle, mine.DisplayName);
    }

    public async Task Unfriend(string account)
    {
        await Owner();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var friend = ((await Profile.Read()).Friends ?? []).FirstOrDefault(row => row.Confirmed && row.Account == account);
        if (friend is null) { return; }

        await Profile.RemoveFriendAccount(account);
        await Door.DropAccount(account);
        // The other side may already be gone or may refuse; this side is unlinked either way.
        try { await SiloNeurons.Get<IFriendLinks>(services, FriendLinksDefaults.Id(friend.BrainId)).Unfriended(); }
        catch (UnauthorizedAccessException refused) { OsConsoleLog.For<FriendLinksNeuron>().LogSwallowed(refused, "the other side refused the unfriend notice"); }
    }

    public async Task Unfriended()
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        if (brain == Id.Brain) { throw PersonActs.Refused(); }

        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        await Profile.RemoveFriendAccount(account);
        await Door.DropAccount(account);
    }

    // The brain's owner, as a person, in this brain.
    private async Task<string> Owner()
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        return brain == Id.Brain ? account : throw PersonActs.Refused();
    }

    private static (string Brain, string Secret) Parse(string? code)
    {
        var text = (code ?? string.Empty).Trim();
        var dot = text.LastIndexOf('.');
        var secret = dot < 0 ? string.Empty : text[(dot + 1)..].Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (dot <= 0 || secret.Length != 12 || secret.Any(c => !Alphabet.Contains(c, StringComparison.Ordinal)) || !DigitalBrain.BrainId.IsValid(text[..dot]))
        {
            throw new ArgumentException("That is not a friend code.", nameof(code));
        }

        return (text[..dot], secret);
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));
}
