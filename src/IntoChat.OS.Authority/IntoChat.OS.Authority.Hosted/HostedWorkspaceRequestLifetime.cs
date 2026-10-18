using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Authority.Hosted;

// Authentication at request start is insufficient for watches and other active streams.
internal static class HostedWorkspaceRequestLifetime
{
    internal static async Task RunAsync(HttpContext http, string grantId, DateTimeOffset expires,
        HostedWorkspaceStore store, TimeProvider clock, RequestDelegate next)
    {
        var remaining = expires - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) { http.Response.StatusCode = 401; return; }
        using var expiration = new CancellationTokenSource(remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, expiration.Token);
        http.RequestAborted = lifetime.Token;
        using var abort = lifetime.Token.Register(http.Abort);
        var monitor = MonitorAsync();
        try { await next(http); }
        finally { abort.Dispose(); await lifetime.CancelAsync(); await monitor; }

        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    if (!await store.IsActiveAsync(grantId, lifetime.Token))
                    { await lifetime.CancelAsync(); return; }
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception) { await lifetime.CancelAsync(); }
        }
    }
}
