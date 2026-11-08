using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Space;

namespace IntoChat.OS.Modules.Flutter;

// The Space's profile, post and posts records kept the JSON of the widget states they were: a stored record still deserialises.
public sealed class SpaceRecordsFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingSpaceStateJsonStillDeserialises(bool web)
    {
        var options = web ? JsonSerializerOptions.Web : JsonSerializerOptions.Default;
        var person = Read<PersonState>("""{"Name":"b/profile","Version":3,"DisplayName":"Ada","AvatarUrl":"https://a.test/a.png"}""", options);
        Assert.Equal(("b/profile", 3, "Ada", "https://a.test/a.png"), (person.Name, person.Version, person.DisplayName, person.AvatarUrl));

        var card = Read<CardState>("""{"Name":"b/posts/p1","Version":2,"Title":"Hello","Body":"World","Children":[{"Kind":"view","Name":"b/apps/x/view"}]}""", options);
        Assert.Equal(("Hello", "World"), (card.Title, card.Body));
        Assert.Equal(new ViewRef("view", "b/apps/x/view"), Assert.Single(card.Children));

        var posts = Read<CollectionState>("""{"Name":"b/posts","Revision":1,"Definition":{"Items":[{"Id":"b/posts/p1","Label":"Hello","Kind":"post"}],"FileMode":false,"SelectionEnabled":false,"EmptyLabel":"No posts yet"}}""", options);
        Assert.Equal("Hello", Assert.Single(posts.Definition.Items).Label);
        Assert.False(posts.Definition.FileMode);
    }

    [Fact]
    public void SpaceRecordsRoundTrip()
    {
        var records = new SpaceRecords
        {
            Profile = new() { Name = "b/profile", Version = 1, DisplayName = "Ada" },
            Posts = [new() { Name = "b/posts/p1", Version = 1, Title = "Hello", Body = "World" }],
        };
        var back = Read<SpaceRecords>(JsonSerializer.Serialize(records), JsonSerializerOptions.Default);
        Assert.Equal("Ada", back.Profile.DisplayName);
        Assert.Equal("Hello", Assert.Single(back.Posts).Title);
    }

    private static T Read<T>(string json, JsonSerializerOptions options) => JsonSerializer.Deserialize<T>(json, options) ?? throw new InvalidOperationException();
}
