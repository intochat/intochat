using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// The authenticated edge. A cookie session (issued by the identity endpoints) or the configured
// Basic bootstrap credential decides the principal; only the declared Open posture lets an
// anonymous request act as the synthetic owner. The resolved person is entered through EdgeAuthority for the
// rest of the request, so it travels with grain calls.
public static class AccountSession
{
    public const string DefaultLogin = IntoChat.OS.Modules.Identity.Authority.DefaultOwner.Login;
    public const string CheckPath = "/auth/check";

    private const string BasicScheme = "Basic";

    // Generous for "user:pass" while keeping the decode buffer stack-safe.
    private const int MaxEncodedCredentialChars = 1024;

    // Probed by the shell's login screen and by container probes; never gated.
    private static readonly string[] AnonymousPaths = ["/health", "/ready", "/alive"];

    public static WebApplication UseAccountSession(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var credential = BasicCredential.FromOptions(options);
        // Closed beyond loopback (NetworkSignIn): the route is not mapped there, and the path is not let through either.
        var openRegistration = NetworkSignInGuard.Configured(app.Services).OpenRegistration;

        app.Use(async (context, next) =>
        {
            if (IsAnonymous(context.Request, openRegistration) || context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var principal = ResolvePrincipal(context, options.Posture, credential);
            if (principal is null)
            {
                // No WWW-Authenticate: the browser's native Basic prompt would race the Flutter
                // login screen and cannot be dismissed from inside the app.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // However a guest identity was made, it passes only the endpoints marked for guests.
            // Unmatched routes (endpoint is null) fall through cleanly to 404 / static SPA fallback
            // rather than falsely returning 403 just because the visitor carries a try-* cookie.
            var endpoint = context.GetEndpoint();
            if (GuestSurface.Is(principal) && endpoint is not null && !IsGuestAllowed(endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (GuestSurface.Is(principal))
            {
                context.RequestServices.GetService<DigitalBrain.Brains.IBrainResidencyRegistry>()?.Touch(principal.Brain.Value);
            }

            using var entered = Edge(context).Enter(principal);
            // The framework's session surfaces (the journal tail among them) ask for an authenticated user with the principal
            // claim, as a cookie session carries. The Basic operator and the Open owner have no cookie, so those routes alone are
            // shown the resolved caller as that user. Any other route still sees an unauthenticated user for them: the runtime
            // observation and the circle invitation read that as "not a real session" on purpose.
            if (context.User.Identity?.IsAuthenticated != true && endpoint?.Metadata.GetMetadata<FrameworkSurface>() is not null)
            {
                context.User = SessionUser(principal);
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    private static bool IsGuestAllowed(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<DigitalBrain.Runtime.Http.BrainRouteGroups.GuestClosed>() is null
            && (endpoint.Metadata.GetMetadata<GuestSurface.Door>() is not null
                || endpoint.Metadata.GetMetadata<DigitalBrain.Runtime.Http.BrainRouteGroups.GuestBrainDoor>() is not null);

    // Marks the endpoints of the framework's module HTTP surfaces group (ModuleEndpoints).
    internal sealed class FrameworkSurface
    {
        public static readonly FrameworkSurface Instance = new();
    }

    internal const string LocalSessionScheme = "IntoChatOS.LocalCaller";

    // The user a cookie session would have been for this caller: the same claim names the framework's membership check reads.
    public static ClaimsPrincipal SessionUser(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var person = caller.For ?? throw new UnauthorizedAccessException("Only a caller acting for a person has a session user.");
        return new(new ClaimsIdentity(
        [
            new Claim(DigitalBrain.Capabilities.Identity.SessionClaims.Principal, person.Principal),
            new Claim(DigitalBrain.Capabilities.Identity.SessionClaims.Account, person.Account),
            new Claim(DigitalBrain.Capabilities.Identity.SessionClaims.Brain, caller.Brain.Value),
        ], LocalSessionScheme));
    }

    // A cookie session wins; otherwise the configured Basic bootstrap credential; otherwise only
    // the declared Open posture lets the request act as the synthetic owner — Secured answers 401.
    public static Caller? ResolveCaller(HttpContext context, AuthOptions options)
        => ResolvePrincipal(context, options.Posture, BasicCredential.FromOptions(options));

    // The one seam for "which brain is this caller using": the session may declare a brain, else
    // the owner brain that both the synthetic Open owner and the configured Basic operator are
    // members of. Registered users always carry the brain claim (IdentityHostOptions.BrainClaim), so they never fall back.
    internal static string DefaultBrain(string accountId, string? claimBrain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return claimBrain is { Length: > 0 } ? claimBrain : DefaultLogin;
    }

    // The session's claim names: the host's identity options, or their defaults on a host that never bound them.
    internal static IdentityHostOptions IdentityOf(HttpContext context)
        => context.RequestServices?.GetService<IOptions<IdentityHostOptions>>()?.Value ?? DefaultIdentity;

    private static readonly IdentityHostOptions DefaultIdentity = new();

    // The member a sign-in just proved, stamped exactly as their next authenticated request will be.
    internal static Caller ForMember(EdgeAuthority edge, Member member)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(member);
        return edge.Person(member.PrincipalId, member.AccountId, new BrainId(member.BrainId));
    }

    private static EdgeAuthority Edge(HttpContext context) => context.RequestServices.GetRequiredService<EdgeAuthority>();

    private static Caller? ResolvePrincipal(HttpContext context, IdentityPosture? posture, BasicCredential? credential)
    {
        var user = context.Items.TryGetValue(Day0EphemeralBootstrap.MintedPrincipalItemKey, out var minted) && minted is ClaimsPrincipal mintedPrincipal
            ? mintedPrincipal
            : context.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            var principalId = DigitalBrain.Runtime.Http.SessionPrincipal.PrincipalId(user);
            if (!string.IsNullOrWhiteSpace(principalId))
            {
                var accountId = user.FindFirst(DigitalBrain.Capabilities.Identity.SessionClaims.Account)?.Value ?? principalId;
                var claimBrain = user.FindFirst(DigitalBrain.Capabilities.Identity.SessionClaims.Brain)?.Value;
                return Build(principalId, accountId, context, claimBrain);
            }
        }

        if (credential is not null && credential.Matches(context.Request.Headers.Authorization))
        {
            var owner = string.Equals(credential.Username, DefaultLogin, StringComparison.Ordinal);
            return Build(owner ? DefaultLogin : credential.Username, owner ? DefaultLogin : credential.Username, context);
        }

        if (posture != IdentityPosture.Open)
        {
            return null;
        }

        var presenceProof = context.RequestServices?.GetService<IPresenceProof>();
        if (context.Request.Headers.TryGetValue(PresenceProof.HeaderName, out var presenceHeaders) && presenceHeaders.Count > 0)
        {
            if (presenceHeaders.Count == 1
                && presenceProof is not null
                && presenceProof.MatchesHeader(presenceHeaders[0]))
            {
                return Build(DefaultLogin, DefaultLogin, context);
            }

            // An explicit presence header that is missing a validator or fails constant-time verification
            // fails closed with 401 so stale shells refresh rather than downgrading silently.
            return null;
        }

        if (context.Request.Cookies.TryGetValue(PresenceProof.CookieName, out var presenceCookie)
            && !string.IsNullOrEmpty(presenceCookie))
        {
            if (presenceProof is not null
                && PresenceProofValidator.IsSameOriginRequest(context.Request)
                && presenceProof.MatchesCookie(presenceCookie))
            {
                return Build(DefaultLogin, DefaultLogin, context);
            }

            return null;
        }

        return null;
    }

    private static Caller? Build(string principalId, string accountId, HttpContext context, string? claimBrain = null)
    {
        var brainId = context.Request.RouteValues.TryGetValue("brainId", out var routeBrain)
            && routeBrain is string { Length: > 0 } brainValue
                ? brainValue
                : DefaultBrain(accountId, claimBrain);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        // A route naming no valid brain identifies no caller.
        if (!BrainId.IsValid(brainId)) { return null; }
        var edge = Edge(context);
        var brain = new BrainId(brainId);
        // A guest never inherits Actor.Person: it is stamped as the guest app.
        return GuestSurface.IsGuestPrincipal(principalId) ? edge.Guest(principalId, accountId, brain) : edge.Person(principalId, accountId, brain);
    }

    private static bool IsAnonymous(HttpRequest request, bool openRegistration)
    {
        // Preflight carries no Authorization header by design; CORS answers it.
        if (HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        // The login, session, and residency endpoints exist to establish or inspect the session the gate would require.
        var requested = request.Path.Value?.ToLowerInvariant();
        if (requested is "/identity/session" or "/identity/residency" or "/identity/logout" or PresenceProof.ExchangePath)
        {
            return true;
        }

        if (requested is "/identity/login" && IdentityOf(request.HttpContext).BuiltInPasswordEndpoints)
        {
            return true;
        }

        if (requested is "/identity/register" && openRegistration)
        {
            return true;
        }

        foreach (var path in AnonymousPaths)
        {
            if (request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class BasicCredential
    {
        private readonly byte[] _expected;

        private BasicCredential(string username, string password)
            => _expected = Encoding.UTF8.GetBytes($"{username}:{password}");

        public string Username { get; private init; } = "";

        public static BasicCredential? FromOptions(AuthOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var username = options.Username;
            var password = options.Password;

            return string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
                ? null
                : new BasicCredential(username, password) { Username = username };
        }

        public bool Matches(string? authorizationHeader)
        {
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                return false;
            }

            var separator = authorizationHeader.IndexOf(' ');
            if (separator <= 0
                || !authorizationHeader.AsSpan(0, separator).Equals(BasicScheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var encoded = authorizationHeader.AsSpan(separator + 1).Trim();

            // Bounded before the stackalloc: the header length is attacker-controlled.
            if (encoded.Length is 0 or > MaxEncodedCredentialChars)
            {
                return false;
            }

            Span<byte> presented = stackalloc byte[MaxEncodedCredentialChars / 4 * 3];
            if (!Convert.TryFromBase64Chars(encoded, presented, out var written))
            {
                return false;
            }

            // FixedTimeEquals over the whole "user:pass" pair: no length or prefix leak.
            return CryptographicOperations.FixedTimeEquals(presented[..written], _expected);
        }
    }
}
