using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Http;

internal sealed class IpThrottleFilter(AuthCalls calls, AuthHashing hashing) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var prefix = IpPrefix.Truncate(context.HttpContext.Connection.RemoteIpAddress?.ToString());
        var hit = await calls.IpThrottle(hashing.KeyHash(AuthHashing.IpPurpose, prefix), throttle => throttle.Hit());
        return hit.Allowed ? await next(context)
            : Results.Json(new { code = "RATE_LIMITED", retryAt = hit.RetryAt }, statusCode: 429);
    }
}
