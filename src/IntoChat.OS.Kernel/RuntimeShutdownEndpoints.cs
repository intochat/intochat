using System.Net;
using IntoChat.OS.Authority.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel;

/// <summary>
/// Loopback-only lifecycle control endpoints authenticated by the host's <see cref="IPresenceProof"/>
/// so <c>intochat --quit</c> can trigger a clean, journal-flushed shutdown without duplicating
/// presence-token readers or reading plaintext tokens from environment variables.
/// </summary>
public static class RuntimeShutdownEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/runtime").AllowAnonymous();

        group.MapGet("/status", (HttpContext http) =>
        {
            if (!IsLoopbackRequest(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var proof = http.RequestServices.GetService<IPresenceProof>();
            if (!HasValidPresenceProof(http.Request, proof))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                status = "running",
                pid = Environment.ProcessId,
                uptimeMs = Environment.TickCount64,
            });
        });

        group.MapPost("/shutdown", (
            HttpContext http,
            [FromServices] IHostApplicationLifetime lifetime,
            [FromServices] ILoggerFactory loggers) =>
        {
            if (!IsLoopbackRequest(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var proof = http.RequestServices.GetService<IPresenceProof>();
            if (!HasValidPresenceProof(http.Request, proof))
            {
                return Results.Unauthorized();
            }

            var logger = loggers.CreateLogger("IntoChat.OS.Kernel.Lifecycle");
            logger.LogInformation("Graceful kernel shutdown requested via authenticated loopback presence proof (PID {Pid}).", Environment.ProcessId);
            lifetime.StopApplication();

            return Results.Ok(new
            {
                stopping = true,
                pid = Environment.ProcessId,
            });
        });
    }

    public static bool IsLoopbackRequest(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var remoteIp = http.Connection.RemoteIpAddress;
        return remoteIp is null || IPAddress.IsLoopback(remoteIp);
    }

    public static bool HasValidPresenceProof(HttpRequest request, IPresenceProof? proof)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (proof is null)
        {
            return false;
        }

        var candidate = ExtractCandidateToken(request);
        return !string.IsNullOrEmpty(candidate) && proof.MatchesHeader(candidate);
    }

    private static string? ExtractCandidateToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(PresenceProof.LegacyHeaderName, out var direct) && direct.Count == 1)
        {
            var token = direct[0]?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        if (request.Headers.TryGetValue(PresenceProof.HeaderName, out var canonical) && canonical.Count == 1)
        {
            var token = canonical[0]?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        if (request.Headers.TryGetValue("Authorization", out var authHeader) && authHeader.Count == 1)
        {
            var raw = authHeader[0]?.Trim();
            const string BearerPrefix = "Bearer ";
            if (raw is not null && raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var bearer = raw[BearerPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(bearer))
                {
                    return bearer;
                }
            }
        }

        return null;
    }
}
