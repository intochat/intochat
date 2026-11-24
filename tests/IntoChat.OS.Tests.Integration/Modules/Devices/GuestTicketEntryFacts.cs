using System.Net;
using System.Security.Cryptography;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Modules.Devices;

// Owner decision D3: guest try runs on the OS install, entered with a guest ticket from a trusted issuer listed with Guests.
// The guest gets its own issuer-scoped brain and a guest session that passes only the endpoints marked for guests.
[Trait("Category", "AccessFindings")]
public sealed class GuestTicketEntryFacts(CloudEdgeHost os) : IClassFixture<CloudEdgeHost>
{
    private const string GuestPath = "/identity/external/guest";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Ticket(string session, string issuer = CloudEdgeHost.Cloud, ECDsa? key = null, string audience = CloudEdgeHost.Audience)
        => GuestTicket.Sign(new GuestTicketClaims(issuer, audience, session, Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddHours(1)), key ?? os.CloudKey, TimeProvider.System);

    // A browser of its own: cookies kept, redirects not followed.
    private HttpClient Browser() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
    {
        BaseAddress = os.Host.BaseAddress,
    };

    private static string Enter(string ticket, string returnUrl = "/") => $"{GuestPath}?t={Uri.EscapeDataString(ticket)}&returnUrl={Uri.EscapeDataString(returnUrl)}";

    [Fact]
    public async Task AGuestTicket_MakesTheIssuerScopedGuest_AndAGuestSessionThatPassesOnlyGuestDoors()
    {
        var session = "s" + Guid.NewGuid().ToString("N")[..12];
        var guest = GuestSurface.PrincipalOf(CloudEdgeHost.Cloud, session);
        using var browser = Browser();

        using var entered = await browser.GetAsync(Enter(Ticket(session)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, entered.StatusCode);
        Assert.Equal("/", entered.Headers.Location?.OriginalString);
        Assert.True(await os.Host.Services.GetRequiredService<IIdentityClient>().BrainExists(guest));

        // The guest's session is a guest's: a route without the guest door answers 403 to it, and 401 to nobody at all.
        using var asGuest = await browser.GetAsync($"/brains/{guest}/devices/this", Ct);
        using var anonymous = await os.Host.Client.GetAsync($"/brains/{guest}/devices/this", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, asGuest.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, anonymous.StatusCode);
    }

    [Fact]
    public async Task OneTryGuest_PerIssuerAndSession()
    {
        Assert.Equal(GuestSurface.PrincipalOf("cloud", "s1"), GuestSurface.PrincipalOf("cloud", "s1"));
        Assert.NotEqual(GuestSurface.PrincipalOf("cloud", "s1"), GuestSurface.PrincipalOf("other", "s1"));
        Assert.NotEqual(GuestSurface.PrincipalOf("cloud", "s1"), GuestSurface.PrincipalOf("cloud", "s2"));
        Assert.True(GuestSurface.IsGuestPrincipal(GuestSurface.PrincipalOf("cloud", "s1")));
        Assert.True(DigitalBrain.BrainId.IsValid(GuestSurface.PrincipalOf("cloud", "s1")));

        // The same session, entered twice with two tickets, is the same guest.
        var session = "s" + Guid.NewGuid().ToString("N")[..12];
        using var first = await Browser().GetAsync(Enter(Ticket(session)), Ct);
        using var again = await Browser().GetAsync(Enter(Ticket(session)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
    }

    [Fact]
    public async Task ATicketIsRedeemedOnce()
    {
        var ticket = Ticket("s" + Guid.NewGuid().ToString("N")[..12]);

        using var first = await Browser().GetAsync(Enter(ticket), Ct);
        using var replay = await Browser().GetAsync(Enter(ticket), Ct);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task AnIssuerNotListedForGuests_AnotherAudience_ASignInTicket_OrAForeignReturnUrl_IsRefused()
    {
        var session = "s" + Guid.NewGuid().ToString("N")[..12];
        var signIn = ExternalSignInTicket.Sign(new ExternalSignInClaims(CloudEdgeHost.Cloud, CloudEdgeHost.Audience, "ada@example.com", "Ada", true, "pwd",
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(30)), os.CloudKey, TimeProvider.System);
        string[] refused =
        [
            Enter(Ticket(session, issuer: CloudEdgeHost.SignInOnly, key: os.SignInKey)),
            Enter(Ticket(session, key: os.SignInKey)),
            Enter(Ticket(session, audience: "https://elsewhere.test")),
            Enter(signIn),
            Enter(Ticket(session), returnUrl: "https://evil.test/"),
        ];

        foreach (var target in refused)
        {
            using var response = await Browser().GetAsync(target, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.False(await os.Host.Services.GetRequiredService<IIdentityClient>().BrainExists(GuestSurface.PrincipalOf(CloudEdgeHost.Cloud, session)));
    }

    [Fact]
    public async Task AGuestTicketIsNotASignInTicket()
    {
        using var response = await Browser().GetAsync($"/identity/external?t={Uri.EscapeDataString(Ticket("s1"))}&returnUrl=%2F", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
