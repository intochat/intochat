using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Flutter.Space;
using TestOS = DigitalBrain.Testing.TestOS;

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

    // The Space owns its records: its person writes the profile and a post, and both read back after a restart.
    [Fact]
    public async Task TheSpaceWritesItsProfileAndPostsAndReadsThemAfterARestart()
    {
        await using var os = Space();
        var id = new NeuronId(new BrainId("owner"), ISpace.Type, "main");
        using (RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", "owner")))
        {
            var space = os.Get<ISpace>(id);
            await space.SetProfile("Ada", "https://a.test/a.png");
            await space.Post("p1", "Hello", "World");
            await space.Post("p1", "Hello again", "World");
        }

        await os.Restart();

        var records = await os.Get<ISpaceRecords>(id).Records();
        Assert.Equal(("owner/profile", "Ada", 1), (records.Profile.Name, records.Profile.DisplayName, records.Profile.Version));
        var post = Assert.Single(records.Posts);
        Assert.Equal(("owner/posts/p1", "Hello again", 2), (post.Name, post.Title, post.Version));
    }

    [Fact]
    public async Task OnlyTheBrainsOwnPersonWritesItsSpace()
    {
        await using var os = Space();
        using var stranger = RuntimeCallerContext.Stamp(new CapabilityCaller("p2", "a2", "someone-else"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<ISpace>(new NeuronId(new BrainId("owner"), ISpace.Type, "main")).SetProfile("Eve", ""));
    }

    // A host with no composition services: the Space's records need none (its scene rebuild finds no kernel and gives up).
    private static TestOS Space()
        => TestOS.Local(options => options.Services = global::Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new global::Microsoft.Extensions.DependencyInjection.ServiceCollection())).Register(typeof(FlutterModule).Assembly);

    private static T Read<T>(string json, JsonSerializerOptions options) => JsonSerializer.Deserialize<T>(json, options) ?? throw new InvalidOperationException();
}
