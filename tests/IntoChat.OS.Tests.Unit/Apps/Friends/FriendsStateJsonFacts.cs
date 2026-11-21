using System.Text;
using DigitalBrain.Invocation;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Settings;

namespace IntoChat.OS.Tests.Unit.Apps.Friends;

public sealed class FriendsStateJsonFacts
{
    [Fact]
    public void OldFriendsScreenStateStillLoads()
    {
        const string json =
            """{"handle":"ada","displayName":"Ada","bio":"","pinnedApps":[],"friends":[{"brainId":"ben","handle":"ben","displayName":"Ben","scope":"friends","account":"ben","confirmed":true}],"posts":[],"selectedScope":"all","status":"Ready","pendingInviteCode":"x"}""";
        var state = (FriendsAppState)RuntimeProtocol.Deserialize(Encoding.UTF8.GetBytes(json), typeof(FriendsAppState))!;
        Assert.Equal(("ada", 0), (state.Handle, state.Roster.Length));
    }

    [Fact]
    public void OldSettingsStateWithTeamFriendsStillLoads()
    {
        const string json =
            """{"title":"Settings","status":"Ready","displayName":"Ada","theme":"dark","defaultModel":"qwen3.5:4b","teamFriends":[{"brainId":"ben","handle":"ben","displayName":"Ben","scope":"friends","account":"ben","confirmed":true}]}""";
        var state = (SettingsAppState)RuntimeProtocol.Deserialize(Encoding.UTF8.GetBytes(json), typeof(SettingsAppState))!;
        Assert.Equal("Ada", state.DisplayName);
        Assert.Null(state.TeamRoster);
    }
}
