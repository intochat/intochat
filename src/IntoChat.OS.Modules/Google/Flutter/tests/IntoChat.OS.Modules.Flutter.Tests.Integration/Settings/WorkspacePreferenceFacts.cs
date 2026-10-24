using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Settings;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;

public sealed class WorkspacePreferenceFacts(FlutterHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(IntoChat.OS.Testing.Person person) => $"/brains/{person.BrainId}/preferences/workspace";

    private async Task Install(IntoChat.OS.Testing.Person person, string name, string title)
        => await host.Brain.Get<IApp>(IApp.Key(person.BrainId, name)).Create(new AppManifest(title, title, [], [], []), "spec");

    private static string[] Names(JsonElement body) => [.. body.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Fact]
    public async Task DefaultsToInoWhenUnset()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "ino", "Ino");
        await Install(person, "notes", "Notes");
        await Install(person, "desktop", "Desktop");

        var body = await person.Http.GetFromJsonAsync<JsonElement>(Url(person), Ct);

        Assert.Equal("ino", body.GetProperty("newTabApp").GetString());
        Assert.Equal(["ino", "notes"], Names(body));
        Assert.Equal("Ino", body.GetProperty("choices")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task PutPersistsPerBrain()
    {
        using var a = await host.PersonAsync(ct: Ct);
        using var b = await host.PersonAsync(ct: Ct);
        await Install(a, "notes", "Notes");
        await Install(b, "notes", "Notes");

        using var put = await a.Http.PutAsJsonAsync(Url(a), new { newTabApp = "notes" }, Ct);
        var reread = await a.Http.GetFromJsonAsync<JsonElement>(Url(a), Ct);
        var other = await b.Http.GetFromJsonAsync<JsonElement>(Url(b), Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("notes", (await put.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("newTabApp").GetString());
        Assert.Equal("notes", reread.GetProperty("newTabApp").GetString());
        Assert.Equal("ino", other.GetProperty("newTabApp").GetString());
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("nope")]
    public async Task UnknownOrHiddenAppIs400(string name)
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "desktop", "Desktop");

        using var put = await person.Http.PutAsJsonAsync(Url(person), new { newTabApp = name }, Ct);
        var after = await person.Http.GetFromJsonAsync<JsonElement>(Url(person), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("Choose an installed app.", (await put.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal("ino", after.GetProperty("newTabApp").GetString());
    }

    [Fact]
    public async Task AppCallersAreRefused()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "notes", "Notes");
        IResult result;
        using (person.AsApp(person.Key("apps/notes")))
        {
            result = await WorkspacePreferenceEndpoints.Write(new("notes"), host.Brain, Ct);
        }
        var after = await person.Http.GetFromJsonAsync<JsonElement>(Url(person), Ct);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("ino", after.GetProperty("newTabApp").GetString());
    }

    [Fact]
    public async Task NonMembersAreRefused()
    {
        using var alice = await host.PersonAsync(ct: Ct);
        using var bob = await host.PersonAsync(ct: Ct);

        using var read = await bob.Http.GetAsync(Url(alice), Ct);
        using var put = await bob.Http.PutAsJsonAsync(Url(alice), new { newTabApp = "ino" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task NewTabAppFallsBackToInoWhenTheStoredAppIsGone()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "notes", "Notes");
        using var put = await person.Http.PutAsJsonAsync(Url(person), new { newTabApp = "notes" }, Ct);
        await host.Brain.Get<IApp>(IApp.Key(person.BrainId, "notes")).Delete();

        var body = await person.Http.GetFromJsonAsync<JsonElement>(Url(person), Ct);

        Assert.Equal("ino", body.GetProperty("newTabApp").GetString());
        Assert.DoesNotContain("notes", Names(body));
    }

    [Fact]
    public void HiddenMatchesTheDesktopList()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "features", "os", "Desktop", "Desktop.cs"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "features", "os", "Desktop", "Desktop.cs"));
        var list = Regex.Match(source, @"Hidden\s*=\s*\[(?<names>[^\]]*)\]").Groups["names"].Value;
        var desktop = Regex.Matches(list, "\"([^\"]+)\"").Select(m => m.Groups[1].Value);

        Assert.Equal(desktop.Order(), WorkspacePreferenceEndpoints.Hidden.Order());
    }
}
