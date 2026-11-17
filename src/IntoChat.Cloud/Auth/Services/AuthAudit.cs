using DigitalBrain.Runtime.Http.Surface;
using IntoChat.Cloud.Auth.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Auth.Services;

// The single audit helper every flow calls (CV-6). Entries hold an IP prefix, a user-agent family and closed-vocabulary
// detail; never codes, tokens or addresses. Recording never fails the caller's flow.
internal sealed class AuthAudit(AuthCalls calls, TimeProvider clock, ILogger<AuthAudit> logger)
{
    public async Task RecordAsync(string? principalId, AuthAuditKind kind, string outcome, HttpContext? http, string? detail = null)
    {
        var entry = new AuthAuditEntry(clock.GetUtcNow(), principalId, kind, outcome,
            IpPrefix.Truncate(http?.Connection.RemoteIpAddress?.ToString()), UserAgentFamily(http?.Request.Headers.UserAgent.ToString()), detail);
        try
        {
            var membership = principalId is null ? null : await calls.Membership(principalId, m => m.Get());
            if (membership is null)
            {
                await calls.SystemAudit(audit => audit.Append(entry));
            }
            else
            {
                await calls.AuditLog(membership.OrgBrain, audit => audit.Append(entry));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Auth audit entry {Kind} was not recorded: {Error}", kind, error.GetType().Name);
        }
    }

    internal static string UserAgentFamily(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return "Other";
        }

        if (userAgent.Contains("Edg/", StringComparison.Ordinal))
        {
            return "Edge";
        }

        if (userAgent.Contains("Firefox/", StringComparison.Ordinal))
        {
            return "Firefox";
        }

        if (userAgent.Contains("Chrome/", StringComparison.Ordinal))
        {
            return "Chrome";
        }

        return userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari" : "Other";
    }
}
