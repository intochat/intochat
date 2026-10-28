using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class AccountProfileFacts(AuthHost host)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3];

    private async Task<HttpClient> SecondSessionAsync(AuthUser user)
    {
        var client = host.CookieClient();
        var begin = await AuthHost.ReadAsync(await host.PostAsync(client, "/auth/login", new { email = user.Email, password = user.Password }));
        var challengeId = AuthHost.Parse(begin.Body).GetProperty("challengeId").GetString();
        var code = await host.CodeAsync(user.Email, MailTemplateId.LoginCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(client, $"/auth/login/challenges/{challengeId}/verify", new { method = "email", code })).StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> PutAvatar(HttpClient client, byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return client.PutAsync("/auth/avatar", content, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonOf(HttpClient client, string path) =>
        AuthHost.Parse((await AuthHost.ReadAsync(await client.GetAsync(path, TestContext.Current.CancellationToken))).Body);

    // ---- profile ----

    [Fact]
    public async Task Profile_RequiresASession()
    {
        using var anonymous = host.CookieClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/auth/profile", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Profile_ReturnsSignUpDefaults_AndPersistsAnUpdate()
    {
        using var user = await host.SignUpAsync();
        var first = await JsonOf(user.Client, "/auth/profile");
        Assert.Equal("Test Person", first.GetProperty("displayName").GetString());
        Assert.Equal(user.Email, first.GetProperty("email").GetString());
        Assert.True(first.GetProperty("hasPassword").GetBoolean());
        Assert.False(first.GetProperty("hasTotp").GetBoolean());
        Assert.False(first.GetProperty("hasAvatar").GetBoolean());

        var put = await user.Client.PutAsJsonAsync("/auth/profile", new { displayName = "  Ada King ", language = "ru", theme = "dark", timeZone = "Europe/Kyiv" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var next = await JsonOf(user.Client, "/auth/profile");
        Assert.Equal("Ada King", next.GetProperty("displayName").GetString());
        Assert.Equal("ru", next.GetProperty("language").GetString());
        Assert.Equal("dark", next.GetProperty("theme").GetString());
        Assert.Equal("Europe/Kyiv", next.GetProperty("timeZone").GetString());
    }

    [Theory]
    [InlineData("", "en", "system", "UTC")]
    [InlineData("Ada", "fr", "system", "UTC")]
    [InlineData("Ada", "en", "neon", "UTC")]
    [InlineData("Ada", "en", "system", "Mars/Base")]
    public async Task Profile_RejectsInvalidUpdates(string name, string language, string theme, string zone)
    {
        using var user = await host.SignUpAsync();
        var put = await user.Client.PutAsJsonAsync("/auth/profile", new { displayName = name, language, theme, timeZone = zone }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Profile_ForGoogleOnlyAccount_ReportsNoPassword()
    {
        var email = AuthHost.UniqueEmail("google-profile");
        using var client = host.GoogleClient();
        using var complete = await new GoogleFacts(host).CompleteAsync(client, email);
        var profile = await JsonOf(client, "/auth/profile");
        Assert.False(profile.GetProperty("hasPassword").GetBoolean());
        Assert.False(profile.GetProperty("hasTotp").GetBoolean());
        Assert.Equal("Google Person", profile.GetProperty("displayName").GetString());
        Assert.Equal(email, profile.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Profile_TotpTracksEnrollmentAndFreshCodeRemoval_AndRevokesOtherSessions()
    {
        using var user = await host.SignUpAsync();
        using var other = await SecondSessionAsync(user);
        Assert.False((await JsonOf(user.Client, "/auth/profile")).GetProperty("hasTotp").GetBoolean());
        var (secret, _) = await new TotpFacts(host).EnrolAsync(user);
        Assert.True((await JsonOf(user.Client, "/auth/profile")).GetProperty("hasTotp").GetBoolean());
        var used = TotpFacts.Code(secret, host.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(user.Client, "/auth/totp/disable", new { code = used })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(user.Client, "/auth/totp/disable", new { code = "xxxxxx" })).StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(user.Client, "/auth/totp/disable", new { code = used })).StatusCode);
        Assert.True((await JsonOf(user.Client, "/auth/profile")).GetProperty("hasTotp").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(user.Client, "/auth/totp/disable", new { code = TotpFacts.Code(secret, host.Clock.GetUtcNow()) })).StatusCode);
        Assert.False((await JsonOf(user.Client, "/auth/profile")).GetProperty("hasTotp").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---- avatar ----

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", false)]
    public async Task Avatar_UploadServeAndRemove(string contentType, bool png)
    {
        using var user = await host.SignUpAsync();
        var bytes = png ? Png : Jpeg;
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync("/auth/avatar", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(user.Client, bytes, contentType)).StatusCode);

        using var served = await user.Client.GetAsync("/auth/avatar", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(contentType, served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.True((await JsonOf(user.Client, "/auth/profile")).GetProperty("hasAvatar").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync("/auth/avatar", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync("/auth/avatar", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Avatar_ChecksMagicBytes_NotTheDeclaredType()
    {
        using var user = await host.SignUpAsync();
        var notAnImage = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAvatar(user.Client, notAnImage, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAvatar(user.Client, Jpeg, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAvatar(user.Client, Encoding.ASCII.GetBytes("GIF89a...."), "image/gif")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync("/auth/avatar", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Avatar_RejectsMoreThanFiveMegabytes_AndAcceptsExactlyFive()
    {
        using var user = await host.SignUpAsync();
        var limit = 5 * 1024 * 1024;
        var exact = new byte[limit];
        Png.CopyTo(exact, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(user.Client, exact, "image/png")).StatusCode);
        var over = new byte[limit + 1];
        Png.CopyTo(over, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PutAvatar(user.Client, over, "image/png")).StatusCode);
        Assert.Equal(limit, (await user.Client.GetByteArrayAsync("/auth/avatar", TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Avatar_RequiresASession()
    {
        using var anonymous = host.CookieClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAvatar(anonymous, Png, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/auth/avatar", TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---- sessions ----

    [Fact]
    public async Task RevokeOthers_KeepsTheCurrentSession_AndEndsEveryOther()
    {
        using var user = await host.SignUpAsync();
        using var other = await SecondSessionAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);

        var response = await user.Client.PostAsync("/auth/sessions/revoke-others", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        var rows = await JsonOf(user.Client, "/auth/sessions");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.True(rows[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task RevokeSession_EndsAnotherSession_NeverTheCurrentOne()
    {
        using var user = await host.SignUpAsync();
        using var other = await SecondSessionAsync(user);
        var rows = await JsonOf(user.Client, "/auth/sessions");
        var current = rows.EnumerateArray().Single(row => row.GetProperty("current").GetBoolean()).GetProperty("handle").GetString();
        var theirs = rows.EnumerateArray().Single(row => !row.GetProperty("current").GetBoolean()).GetProperty("handle").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.DeleteAsync($"/auth/sessions/{current}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.DeleteAsync("/auth/sessions/unknown-handle", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync($"/auth/sessions/{theirs}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---- welcome mail ----

    [Fact]
    public async Task EmailSignUp_SendsExactlyOneWelcome_AndLaterSignInsDoNot()
    {
        using var user = await host.SignUpAsync();
        await host.MailAsync(user.Email, MailTemplateId.Welcome);
        using var second = await SecondSessionAsync(user);
        using var client = host.GoogleClient();
        using var google = await new GoogleFacts(host).CompleteAsync(client, user.Email);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, host.MailCount(user.Email, MailTemplateId.Welcome));
    }

    [Fact]
    public async Task GoogleFirstSignIn_SendsExactlyOneWelcome_AcrossRepeatedSignIns()
    {
        var email = AuthHost.UniqueEmail("google-welcome");
        using var client = host.GoogleClient();
        using var first = await new GoogleFacts(host).CompleteAsync(client, email);
        using var again = await new GoogleFacts(host).CompleteAsync(client, email);
        await host.MailAsync(email, MailTemplateId.Welcome);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, host.MailCount(email, MailTemplateId.Welcome));
    }

    [Fact]
    public async Task ConcurrentFirstGoogleSignIns_SendOneWelcome()
    {
        var email = AuthHost.UniqueEmail("race-welcome");
        var facts = new GoogleFacts(host);
        using var one = host.GoogleClient();
        using var two = host.GoogleClient();
        var results = await Task.WhenAll(facts.CompleteAsync(one, email, sub: "same-sub"), facts.CompleteAsync(two, email, sub: "same-sub"));
        foreach (var result in results) { result.Dispose(); }
        await host.MailAsync(email, MailTemplateId.Welcome);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, host.MailCount(email, MailTemplateId.Welcome));
    }
}
