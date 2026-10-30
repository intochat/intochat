using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

// The authenticated edge. A cookie session (issued by the identity endpoints) or the configured
// Basic bootstrap credential decides the principal; only the declared Open posture lets an
// anonymous request act as the synthetic owner. Resolving a principal stamps a CapabilityCaller on
// the Orleans RequestContext so it travels with grain calls.
public static class AccountSession
{
    public const string DefaultLogin = Authority.DefaultOwner.Login;
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

        app.Use(async (context, next) =>
        {
            if (IsAnonymous(context.Request) || context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
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

            CallerContextStamper.Stamp(principal);
            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    // A cookie session wins; otherwise the configured Basic bootstrap credential; otherwise only
    // the declared Open posture lets the request act as the synthetic owner — Secured answers 401.
    public static CapabilityCaller? ResolveCaller(HttpContext context, AuthOptions options)
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
    internal static CapabilityCaller ForMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return CapabilityCallers.Of(member.PrincipalId, member.AccountId, member.BrainId);
    }

    private static CapabilityCaller? ResolvePrincipal(HttpContext context, IdentityPosture? posture, BasicCredential? credential)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var principalId = DigitalBrain.Runtime.Http.SessionPrincipal.PrincipalId(context.User);
            if (!string.IsNullOrWhiteSpace(principalId))
            {
                var accountId = context.User.FindFirst(DigitalBrain.Capabilities.Identity.SessionClaims.Account)?.Value ?? principalId;
                var claimBrain = context.User.FindFirst(DigitalBrain.Capabilities.Identity.SessionClaims.Brain)?.Value;
                return Build(principalId, accountId, context, claimBrain);
            }
        }

        if (credential is not null && credential.Matches(context.Request.Headers.Authorization))
        {
            var owner = string.Equals(credential.Username, DefaultLogin, StringComparison.Ordinal);
            return Build(owner ? DefaultLogin : credential.Username, owner ? DefaultLogin : credential.Username, context);
        }

        return posture == IdentityPosture.Open ? Build(DefaultLogin, DefaultLogin, context) : null;
    }

    private static CapabilityCaller Build(string principalId, string accountId, HttpContext context, string? claimBrain = null)
    {
        var brainId = context.Request.RouteValues.TryGetValue("brainId", out var routeBrain)
            && routeBrain is string { Length: > 0 } brainValue
                ? brainValue
                : DefaultBrain(accountId, claimBrain);
        return CapabilityCallers.Of(principalId, accountId, brainId);
    }

    private static bool IsAnonymous(HttpRequest request)
    {
        // Preflight carries no Authorization header by design; CORS answers it.
        if (HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        // The login and session endpoints exist to establish the session the gate would require.
        var requested = request.Path.Value?.ToLowerInvariant();
        if (requested is "/identity/session" or "/identity/logout")
        {
            return true;
        }

        if (requested is "/identity/register" or "/identity/login" && IdentityOf(request.HttpContext).BuiltInPasswordEndpoints)
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
