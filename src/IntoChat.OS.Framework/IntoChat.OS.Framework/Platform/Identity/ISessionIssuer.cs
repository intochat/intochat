using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Framework.Platform.Identity;

// Issues and ends the platform session cookie for a member another mechanism has already authenticated.
// The principal, account, brain and role claims always come from the member; additional claims never replace them.
public interface ISessionIssuer
{
    Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default);

    Task EndAsync(HttpContext http);
}

public sealed record SessionRequest(Member Member, IReadOnlyCollection<Claim>? AdditionalClaims = null, TimeSpan? Lifetime = null);

public sealed record SessionIssued(Member Member, DateTimeOffset? ExpiresAt);
