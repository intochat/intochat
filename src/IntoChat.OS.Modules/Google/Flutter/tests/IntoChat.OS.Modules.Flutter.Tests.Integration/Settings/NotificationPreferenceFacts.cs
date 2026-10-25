using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;

public sealed class NotificationPreferenceFacts(FlutterHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Defaults_when_unset()
    {
        using var person = await host.PersonAsync(ct: Ct);

        var prefs = await person.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{person.BrainId}/preferences/notifications", Ct);

        Assert.Equal(new Dictionary<string, bool>
        {
            ["agent-finished"] = true,
            ["needs-approval"] = true,
            ["connection-errors"] = true,
            ["product-updates"] = false,
        }, prefs);
    }

    [Fact]
    public async Task Put_persists_per_brain()
    {
        using var a = await host.PersonAsync(ct: Ct);
        using var b = await host.PersonAsync(ct: Ct);

        using var put = await a.Http.PutAsJsonAsync($"/brains/{a.BrainId}/preferences/notifications",
            new Dictionary<string, bool> { ["agent-finished"] = false, ["product-updates"] = true }, Ct);
        var reread = await a.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{a.BrainId}/preferences/notifications", Ct);
        var other = await b.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{b.BrainId}/preferences/notifications", Ct);
        using var caller = a.AsCaller();
        var stored = await host.Brain.Get<ITextField>(a.BrainId + "/settings/notifications/agent-finished").Read();

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(reread!["agent-finished"]);
        Assert.True(reread["product-updates"]);
        Assert.True(reread["needs-approval"]);
        Assert.True(other!["agent-finished"]);
        Assert.False(other["product-updates"]);
        Assert.Equal("off", stored.Value);
    }

    [Fact]
    public async Task Unknown_key_is_400()
    {
        using var person = await host.PersonAsync(ct: Ct);

        using var put = await person.Http.PutAsJsonAsync($"/brains/{person.BrainId}/preferences/notifications",
            new Dictionary<string, bool> { ["agent-finished"] = false, ["nope"] = true }, Ct);
        var after = await person.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{person.BrainId}/preferences/notifications", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.True(after!["agent-finished"]);
    }

    // An installed app (stamped by the app proxy) cannot change a person's notification preferences.
    [Fact]
    public async Task Installed_app_callers_cannot_write()
    {
        using var person = await host.PersonAsync(ct: Ct);
        IResult result;
        using (person.AsApp(person.Key("apps/notes")))
        {
            result = await IntoChat.OS.Modules.Flutter.Settings.NotificationPreferenceEndpoints.Write(
                new Dictionary<string, bool> { ["agent-finished"] = false }, host.Brain, Ct);
        }
        var after = await person.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{person.BrainId}/preferences/notifications", Ct);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.True(after!["agent-finished"]);
    }
}
