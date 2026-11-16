using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// A friend code names the brain that made it and carries a secret that works once: "{brain}.{XXXX-XXXX-XXXX}".
public sealed record FriendCode(string Code, DateTimeOffset ExpiresAt);

// What one side tells the other when a code is redeemed.
public sealed record FriendIntroduction(string Account, string Brain, string Handle, string DisplayName);

public static class FriendLinksDefaults
{
    public const string TypeName = "friends.links";
    public const string KeyPart = "friends";
    public static DigitalBrain.NeuronId Id(string brain) => new(new DigitalBrain.BrainId(brain), TypeName, KeyPart);
}

// A brain's friend links. A friend is a link both people confirmed: one makes a code, the other redeems it, and each
// brain records the other's account. Nothing is shared with a friend who is not confirmed.
[Shareable, NeuronAlias(FriendLinksDefaults.TypeName), DefaultNeuronType(FriendLinksDefaults.TypeName)]
public interface IFriendLinks
{
    // The brain's owner, acting as a person.
    Task<FriendCode> Mint();
    Task<ProfileFriend> Redeem(string code);
    Task Unfriend(string account);

    // The other person, acting as a person from the brain they own.
    [Shared] Task<FriendIntroduction> Accepted(string codeHash, string handle, string displayName);
    [Shared] Task Unfriended();
}
