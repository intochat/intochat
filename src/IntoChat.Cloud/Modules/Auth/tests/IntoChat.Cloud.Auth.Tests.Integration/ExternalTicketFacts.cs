using System.Net;
using System.Security.Cryptography;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.AspNetCore.WebUtilities;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class ExternalTicketFacts(AuthHost host)
{
    [Fact]
    public async Task Sign_up_for_an_os_install_answers_a_ticket_the_install_verifies()
    {
        var email = AuthHost.UniqueEmail("ticket");
        var redirect = await SignUpToward(email, AuthHost.OsInstall + "/apps/files?view=grid");

        Assert.NotNull(redirect);
        var link = new Uri(redirect);
        Assert.Equal(AuthHost.OsInstall + ExternalTicketIssuer.RedeemPath, link.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(link.Query);
        Assert.Equal("/apps/files?view=grid", query["returnUrl"].ToString());

        using var publicKey = ExternalSignInTicket.ImportPublicKey(Convert.ToBase64String(AuthHost.TicketKey.ExportSubjectPublicKeyInfo()));
        var claims = ExternalSignInTicket.Verify(query["t"].ToString(),
            issuer => issuer == "intochat-cloud" ? (publicKey, AuthHost.OsInstall) : null, host.Clock);
        Assert.NotNull(claims);
        Assert.Equal("intochat-cloud", claims.Issuer);
        Assert.Equal(AuthHost.OsInstall, claims.Audience);
        Assert.Equal(email, claims.Email);
        Assert.Equal("Test Person", claims.Name);
        Assert.True(claims.EmailVerified);
        Assert.Equal("pwd", claims.Amr);
        Assert.False(string.IsNullOrEmpty(claims.Jti));
        Assert.True(claims.ExpiresAt <= host.Clock.GetUtcNow().AddSeconds(60));
    }

    [Fact]
    public async Task A_ticket_for_one_install_does_not_verify_for_another_audience_or_key()
    {
        var redirect = await SignUpToward(AuthHost.UniqueEmail("audience"), AuthHost.OsInstall + "/");
        var ticket = QueryHelpers.ParseQuery(new Uri(redirect!).Query)["t"].ToString();
        using var publicKey = ExternalSignInTicket.ImportPublicKey(Convert.ToBase64String(AuthHost.TicketKey.ExportSubjectPublicKeyInfo()));
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Null(ExternalSignInTicket.Verify(ticket, _ => (publicKey, "https://other.install.test"), host.Clock));
        Assert.Null(ExternalSignInTicket.Verify(ticket, _ => (stranger, AuthHost.OsInstall), host.Clock));
    }

    [Fact]
    public async Task A_return_url_that_is_not_an_os_install_gets_no_ticket()
    {
        Assert.Null(await SignUpToward(AuthHost.UniqueEmail("plain"), AuthHost.OtherReturnOrigin + "/"));
        Assert.Null(await SignUpToward(AuthHost.UniqueEmail("local"), "/"));
        Assert.Null(await SignUpToward(AuthHost.UniqueEmail("refused"), "https://unlisted.example/"));
    }

    [Fact]
    public async Task Google_sign_in_toward_an_os_install_redirects_to_its_external_sign_in()
    {
        var email = AuthHost.UniqueEmail("google-ticket");
        using var client = host.GoogleClient();
        using var complete = await new GoogleFacts(host).CompleteAsync(client, email, returnUrl: AuthHost.OsInstall + "/");
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        var link = complete.Headers.Location!;
        Assert.Equal(AuthHost.OsInstall + ExternalTicketIssuer.RedeemPath, link.GetLeftPart(UriPartial.Path));
        using var publicKey = ExternalSignInTicket.ImportPublicKey(Convert.ToBase64String(AuthHost.TicketKey.ExportSubjectPublicKeyInfo()));
        var claims = ExternalSignInTicket.Verify(QueryHelpers.ParseQuery(link.Query)["t"].ToString(), _ => (publicKey, AuthHost.OsInstall), host.Clock);
        Assert.NotNull(claims);
        Assert.Equal("google", claims.Amr);
        Assert.Equal("Google Person", claims.Name);
    }

    [Fact]
    public async Task Google_with_totp_preserves_return_and_issues_ticket_only_after_factor_proof()
    {
        using var user = await host.SignUpAsync();
        var (secret, _) = await new TotpFacts(host).EnrolAsync(user);
        using var client = host.GoogleClient();
        const string destination = AuthHost.OsInstall + "/apps/files?view=grid&name=a%20b";
        using var complete = await new GoogleFacts(host).CompleteAsync(client, user.Email, returnUrl: destination);
        var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, complete.Headers.Location!).Query);
        Assert.Equal(destination, query["returnUrl"].ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var verified = await AuthHost.ReadAsync(await host.PostAsync(client,
            $"/auth/login/challenges/{query["challengeId"]}/verify?returnUrl={Uri.EscapeDataString(query["returnUrl"].ToString())}",
            new { method = "authenticator", code = TotpFacts.Code(secret, host.Clock.GetUtcNow()) }));
        Assert.Equal(HttpStatusCode.OK, verified.Status);
        var link = new Uri(AuthHost.Parse(verified.Body).GetProperty("redirect").GetString()!);
        Assert.Equal(AuthHost.OsInstall + ExternalTicketIssuer.RedeemPath, link.GetLeftPart(UriPartial.Path));
        var ticketQuery = QueryHelpers.ParseQuery(link.Query);
        Assert.Equal("/apps/files?view=grid&name=a%20b", ticketQuery["returnUrl"].ToString());
        using var publicKey = ExternalSignInTicket.ImportPublicKey(Convert.ToBase64String(AuthHost.TicketKey.ExportSubjectPublicKeyInfo()));
        Assert.NotNull(ExternalSignInTicket.Verify(ticketQuery["t"].ToString(), _ => (publicKey, AuthHost.OsInstall), host.Clock));
    }

    [Theory]
    [InlineData("pwd otp_email", "pwd")]
    [InlineData("pwd otp", "pwd")]
    [InlineData("google", "google")]
    [InlineData("google otp", "google")]
    [InlineData("email otp_email", "otp")]
    public void Auth_methods_map_to_one_ticket_method(string amr, string method) => Assert.Equal(method, ExternalTicketIssuer.Method(amr));

    [Fact]
    public void A_missing_or_foreign_signing_key_is_refused_without_its_value()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => ExternalTicketIssuer.Import(""));
        Assert.Contains("signin-issuer:SigningKey", missing.Message, StringComparison.Ordinal);
        using var rsa = RSA.Create(2048);
        Assert.Throws<InvalidOperationException>(() => ExternalTicketIssuer.Import(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => ExternalTicketIssuer.Import(Convert.ToBase64String(p384.ExportPkcs8PrivateKey())));
    }

    private async Task<string?> SignUpToward(string email, string returnUrl)
    {
        using var client = host.CookieClient();
        var (status, body) = await AuthHost.ReadAsync(await host.SignUpRequestAsync(client, email));
        Assert.Equal(HttpStatusCode.Accepted, status);
        var codeId = AuthHost.Parse(body).GetProperty("codeId").GetString()!;
        var code = await host.CodeAsync(email, MailTemplateId.SignupCode);
        var (verifyStatus, verifyBody) = await AuthHost.ReadAsync(await host.PostAsync(client,
            $"/auth/codes/{codeId}/verify?returnUrl={Uri.EscapeDataString(returnUrl)}", new { code }));
        Assert.Equal(HttpStatusCode.OK, verifyStatus);
        var result = AuthHost.Parse(verifyBody);
        Assert.False(string.IsNullOrEmpty(result.GetProperty("brainId").GetString()));
        return result.TryGetProperty("redirect", out var redirect) ? redirect.GetString() : null;
    }
}
