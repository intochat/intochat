using System.Text.Json;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog.Friends;

public sealed class FriendsJsonFacts
{
    [Fact]
    public void OldFriendLinksJsonStillLoads()
    {
        var r = JsonSerializer.Deserialize(
            """{"pending":[{"hash":"0A1B","expiresAt":"2026-11-21T00:00:00+00:00"}],"refused":3}""",
            FriendsJson.Default.FriendsRecord)!;
        Assert.Equal(("0A1B", 3), (r.Pending.Single().Hash, r.Refused));
        Assert.Null(r.Friends);
        Assert.Equal(0, r.Misses);
        Assert.Empty(r.Groups);
    }
}
