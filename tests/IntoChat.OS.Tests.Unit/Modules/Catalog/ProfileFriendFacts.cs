using System.Text.Json;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

// A friend saved before friend codes existed has no account and no confirmation. It must still load, and load as
// unconfirmed, so nothing saved is lost and nothing can be shared with it until a code is exchanged.
public sealed class ProfileFriendFacts
{
    private const string SavedBeforeFriendCodes =
        """{"BrainId":"ada","Handle":"ada","DisplayName":"Ada Lovelace","Scope":"friends","AddedAt":"2026-11-01T10:00:00+00:00"}""";

    [Fact]
    public void A_friend_saved_in_the_earlier_shape_loads_as_unconfirmed()
    {
        var friend = JsonSerializer.Deserialize<ProfileFriend>(SavedBeforeFriendCodes)!;

        Assert.Equal(("ada", "ada", "Ada Lovelace", "friends"), (friend.BrainId, friend.Handle, friend.DisplayName, friend.Scope));
        Assert.Equal(string.Empty, friend.Account);
        Assert.False(friend.Confirmed);
    }

    [Fact]
    public void A_profile_saved_in_the_earlier_shape_keeps_its_friends()
    {
        var saved = $$"""{"Handle":"me","Bio":"","PinnedApps":[],"Posts":[],"DisplayName":"Me","AvatarUrl":"","Friends":[{{SavedBeforeFriendCodes}}],"SharedApps":[]}""";

        var profile = JsonSerializer.Deserialize<ProfileState>(saved)!;

        var friend = Assert.Single(profile.Friends!);
        Assert.False(friend.Confirmed);
    }

    [Fact]
    public void A_confirmed_friend_round_trips()
    {
        var confirmed = new ProfileFriend("ben", "ben", "Ben", "friends", DateTimeOffset.UnixEpoch, Account: "ben-account", Confirmed: true);

        Assert.Equal(confirmed, JsonSerializer.Deserialize<ProfileFriend>(JsonSerializer.Serialize(confirmed)));
    }
}
