using System.Net;
using System.Text.Json;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.External;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Modules.Devices;

// The mapping step of the cloud/OS split (step 6): a person the cloud signed up into this install before the split signs in through
// the cloud's external ticket to the account and brain they already have, not a new one. Additive and idempotent.
[Trait("Category", "AccessFindings")]
public sealed class PrincipalLinkFacts(CloudEdgeHost os) : IClassFixture<CloudEdgeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IIdentityClient Identity => os.Host.Services.GetRequiredService<IIdentityClient>();
    private PrincipalLinks Links => os.Host.Services.GetRequiredService<PrincipalLinks>();

    private HttpClient Browser() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
    {
        BaseAddress = os.Host.BaseAddress,
    };

    private async Task<HttpStatusCode> SignInAsync(HttpClient browser, string email)
    {
        var ticket = ExternalSignInTicket.Sign(new ExternalSignInClaims(CloudEdgeHost.Cloud, CloudEdgeHost.Audience, email, "Person", true, "pwd",
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(30)), os.CloudKey, TimeProvider.System);
        using var response = await browser.GetAsync($"/identity/external?t={Uri.EscapeDataString(ticket)}&returnUrl=%2F", Ct);
        return response.StatusCode;
    }

    private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task ALinkedPerson_SignsInThroughTheTicket_ToTheBrainTheyAlreadyHave()
    {
        var principal = Unique("cloud-ada");
        var email = Unique("ada") + "@example.com";
        var before = await Identity.RegisterPrincipal(principal, "Ada");

        var linked = Assert.Single(await Links.ApplyAsync([new PrincipalLink(CloudEdgeHost.Cloud, email, principal)], Ct));
        var again = Assert.Single(await Links.ApplyAsync([new PrincipalLink(CloudEdgeHost.Cloud, email.ToUpperInvariant(), principal)], Ct));

        Assert.Equal(("linked", ExternalSignIn.PrincipalOf(CloudEdgeHost.Cloud, email)), (linked.Outcome, linked.Linked));
        Assert.Equal("already-linked", again.Outcome);
        using var browser = Browser();
        Assert.Equal(HttpStatusCode.Redirect, await SignInAsync(browser, email));
        var after = await Identity.CompleteRegistration(linked.Linked);
        Assert.Equal((before.Member.AccountId, before.Member.BrainId), (after!.Member.AccountId, after.Member.BrainId));
        // The ticket's session is a person in that brain.
        using var own = await browser.GetAsync($"/brains/{before.Member.BrainId}/devices/this", Ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        // The existing principal is unchanged.
        Assert.Equal(before.Member.BrainId, (await Identity.CompleteRegistration(principal))!.Member.BrainId);
    }

    [Fact]
    public async Task APersonWhoAlreadySignsInThroughTheTicket_IsAConflict_AndKeepsTheirBrain()
    {
        var email = Unique("bob") + "@example.com";
        using var browser = Browser();
        Assert.Equal(HttpStatusCode.Redirect, await SignInAsync(browser, email));
        var ticketPrincipal = ExternalSignIn.PrincipalOf(CloudEdgeHost.Cloud, email);
        var own = (await Identity.CompleteRegistration(ticketPrincipal))!.Member.BrainId;
        var principal = Unique("cloud-bob");
        await Identity.RegisterPrincipal(principal, "Bob");

        var outcome = Assert.Single(await Links.ApplyAsync([new PrincipalLink(CloudEdgeHost.Cloud, email, principal)], Ct));

        Assert.Equal("conflict", outcome.Outcome);
        Assert.Equal(own, (await Identity.CompleteRegistration(ticketPrincipal))!.Member.BrainId);
    }

    [Fact]
    public async Task AnUnknownPrincipal_AnUntrustedIssuer_OrABadAddress_LinksNothing()
    {
        var outcomes = await Links.ApplyAsync(
        [
            new PrincipalLink(CloudEdgeHost.Cloud, Unique("nobody") + "@example.com", Unique("no-such")),
            new PrincipalLink("stranger", Unique("eve") + "@example.com", Unique("cloud-eve")),
            new PrincipalLink(CloudEdgeHost.Cloud, "not-an-address", Unique("cloud-eve")),
        ], Ct);

        Assert.Equal(["no-such-principal", "untrusted-issuer", "invalid-email"], outcomes.Select(outcome => outcome.Outcome));
    }
}

// The operator's path: the links file named by IntoChatOS:Auth:PrincipalLinks is applied once at start and its result written beside it.
[Trait("Category", "AccessFindings")]
public sealed class PrincipalLinkStartupFacts
{
    [Fact]
    public async Task TheLinksFileIsAppliedAtStart_AndItsResultWrittenBesideIt()
    {
        using var folder = new DigitalBrain.Testing.TempDirectory("principal-links-");
        var file = Path.Combine(folder.Path, "links.json");
        await File.WriteAllTextAsync(file, """[{"issuer":"cloud","email":"ada@example.com","principal":"not-registered"}]""", TestContext.Current.CancellationToken);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        await using (var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(TestContext.Current.CancellationToken, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthKeys.TrustedIssuers + ":0:Name"] = "cloud",
                [AuthKeys.TrustedIssuers + ":0:PublicKey"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                [AuthKeys.TrustedIssuers + ":0:Audience"] = CloudEdgeHost.Audience,
                [AuthKeys.SectionName + ":PrincipalLinks"] = file,
            })))
        {
            Assert.NotNull(host);
        }

        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(file + ".result.json", TestContext.Current.CancellationToken));
        var entry = Assert.Single(result.RootElement.EnumerateArray());
        Assert.Equal("no-such-principal", entry.GetProperty("outcome").GetString());
    }
}
