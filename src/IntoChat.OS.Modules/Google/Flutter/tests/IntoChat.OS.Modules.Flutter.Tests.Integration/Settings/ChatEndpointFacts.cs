using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Settings;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;

public sealed class ChatEndpointFacts(FlutterHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(IntoChat.OS.Testing.Person person, string path = "") => $"/brains/{person.BrainId}/chats{path}";

    private static string NewId() => "c-" + Guid.NewGuid().ToString("N");

    private IChatIndex Index(IntoChat.OS.Testing.Person person) => host.Brain.Get<IChatIndex>(IApp.Key(person.BrainId, "ino") + "/chats");

    private async Task<string> Seed(IntoChat.OS.Testing.Person person, string title, bool archive = true)
    {
        var id = NewId();
        await Index(person).Create(id, title);
        if (archive) { await Index(person).Archive(id); }
        return id;
    }

    private static string[] Ids(JsonElement body) => [.. body.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("id").GetString()!)];

    [Fact]
    public async Task ListsArchivedChatsForMembers()
    {
        using var person = await host.PersonAsync(ct: Ct);
        var archived = await Seed(person, "<b>Old</b> plan");
        await Seed(person, "Live", archive: false);

        var body = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=true", Ct);
        var live = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=false", Ct);

        Assert.Equal([archived], Ids(body));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal("<b>Old</b> plan", body.GetProperty("chats")[0].GetProperty("title").GetString());
        Assert.Equal("Live", live.GetProperty("chats")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnarchiveMovesTheChatBack()
    {
        using var person = await host.PersonAsync(ct: Ct);
        var id = await Seed(person, "Old");

        using var post = await person.Http.PostAsync(Url(person, $"/{id}/unarchive"), null, Ct);
        var archived = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=true", Ct);
        var live = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=false", Ct);

        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        Assert.Empty(Ids(archived));
        Assert.Equal([id], Ids(live));
    }

    [Fact]
    public async Task OtherBrainsAreIsolated()
    {
        using var a = await host.PersonAsync(ct: Ct);
        using var b = await host.PersonAsync(ct: Ct);
        var id = await Seed(a, "Mine");

        var other = await b.Http.GetFromJsonAsync<JsonElement>(Url(b) + "?archived=true", Ct);
        using var post = await b.Http.PostAsync(Url(b, $"/{id}/unarchive"), null, Ct);

        Assert.Empty(Ids(other));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.True((await Index(a).Get(id))!.Archived);
    }

    [Fact]
    public async Task UnknownChatIs404()
    {
        using var person = await host.PersonAsync(ct: Ct);

        using var missing = await person.Http.PostAsync(Url(person, $"/{NewId()}/unarchive"), null, Ct);
        using var malformed = await person.Http.PostAsync(Url(person, "/not-an-id/unarchive"), null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("That chat no longer exists.", (await missing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task NonMembersAreRefused()
    {
        using var alice = await host.PersonAsync(ct: Ct);
        using var bob = await host.PersonAsync(ct: Ct);
        var id = await Seed(alice, "Old");

        using var read = await bob.Http.GetAsync(Url(alice), Ct);
        using var post = await bob.Http.PostAsync(Url(alice, $"/{id}/unarchive"), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.True((await Index(alice).Get(id))!.Archived);
    }

    [Fact]
    public async Task AppCallersAreRefused()
    {
        using var person = await host.PersonAsync(ct: Ct);
        var id = await Seed(person, "Old");
        IResult read, write;
        using (person.AsApp(person.Key("apps/notes")))
        {
            read = await ChatEndpoints.List(host.Brain, Ct);
            write = await ChatEndpoints.Unarchive(id, host.Brain, Ct);
        }

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(read).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(write).StatusCode);
        Assert.True((await Index(person).Get(id))!.Archived);
    }

    private async Task InstallIno(IntoChat.OS.Testing.Person person, Dictionary<string, string>? settings = null)
    {
        var app = host.Brain.Get<IApp>(IApp.Key(person.BrainId, "ino"));
        await app.Create(new AppManifest("Ino", "Ino", [], [], []), "spec");
        if (settings is not null) { await app.Configure(settings, []); }
    }

    [Fact]
    public async Task ListsChatsKeptUnderChatDataAppThroughHttp()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await InstallIno(person, new() { ["ChatDataApp"] = person.BrainId + "/apps/ino-data" });
        // The Ino scripts' key: Ino.DataKey(brain) + "/chats" with ChatDataApp = {brain}/apps/ino-data.
        var inoSide = host.Brain.Get<IChatIndex>(person.BrainId + "/apps/ino-data/chats");
        var id = NewId();
        await inoSide.Create(id, "Kept by Ino");
        await inoSide.Archive(id);
        await Seed(person, "Under the app key", archive: true);

        var body = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=true", Ct);
        using var post = await person.Http.PostAsync(Url(person, $"/{id}/unarchive"), null, Ct);

        Assert.Equal([id], Ids(body));
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        Assert.False((await inoSide.Get(id))!.Archived);
    }

    [Fact]
    public async Task WithoutInoTheListIsEmptyAndUnarchiveIs404()
    {
        using var person = await host.PersonAsync(ct: Ct);

        var body = await person.Http.GetFromJsonAsync<JsonElement>(Url(person) + "?archived=true", Ct);
        using var post = await person.Http.PostAsync(Url(person, $"/{NewId()}/unarchive"), null, Ct);

        Assert.Empty(Ids(body));
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public void IndexKeyFollowsChatDataApp()
    {
        static AppSnapshot Ino(Dictionary<string, string> settings) => new("ino", new AppManifest("Ino", "Ino", [], [], []), "", [], settings, [], "1.0.0",
            AppStatus.Live, [], []);

        Assert.Equal("b1/apps/ino-data/chats", ChatEndpoints.IndexKey("b1", Ino(new() { ["ChatDataApp"] = "b1/apps/ino-data" })));
        Assert.Equal(IApp.Key("b1", "ino") + "/chats", ChatEndpoints.IndexKey("b1", Ino([])));
        Assert.Equal(IApp.Key("b1", "ino") + "/chats", ChatEndpoints.IndexKey("b1", Ino(new() { ["ChatDataApp"] = "" })));
    }
}
