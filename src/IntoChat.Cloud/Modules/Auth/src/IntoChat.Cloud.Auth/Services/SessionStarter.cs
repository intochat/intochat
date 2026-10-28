using System.Globalization;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using IntoChat.Cloud.Auth.Core;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Services;

// The one way Auth signs a member in: the host's ISessionIssuer issues the session (identity@1 ISessions.Issue, which this
// module's port stores in the sessions table) and writes its cookie with auth_time and amr; the row then learns its device.
internal sealed class SessionStarter(ISessionIssuer issuer, IAuthHostSeam host, AuthCalls calls, AuthHashing hashing, AuthModuleOptions options, TimeProvider clock)
{
    public const string SidClaim = SessionPrincipal.SessionIdClaim;
    public const string AuthTimeClaim = "auth_time";
    public const string AmrClaim = "amr";

    public async Task<SessionIssued> StartAsync(HttpContext http, Member member, string amr, CancellationToken ct = default)
    {
        var request = new SessionRequest
        {
            Member = member,
            Claims = new Dictionary<string, string>
            {
                [AuthTimeClaim] = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                [AmrClaim] = amr,
            },
            Lifetime = options.SessionLifetime,
        };
        SessionIssued issued;
        // The runtime reads the module from the stamped caller: only a module granted Issue may issue a session.
        using (AuthSecrets.Stamp(host))
        {
            issued = await issuer.IssueAsync(http, request, ct);
        }

        var label = AuthAudit.UserAgentFamily(http.Request.Headers.UserAgent.ToString()) + " " + IpPrefix.Truncate(http.Connection.RemoteIpAddress?.ToString());
        await calls.Sessions(member.PrincipalId, sessions => sessions.Describe(hashing.SidHash(issued.SessionId), label));
        return issued;
    }

    // The keyed hash of the caller's session id, or null for a session Auth did not issue.
    public string? CurrentSidHash(HttpContext http) =>
        http.User.FindFirst(SidClaim)?.Value is { Length: > 0 } sid ? hashing.SidHash(sid) : null;
}
