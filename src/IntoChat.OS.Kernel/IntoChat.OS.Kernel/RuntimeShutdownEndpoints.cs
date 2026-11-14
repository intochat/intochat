using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel;

/// <summary>
/// Loopback-only lifecycle control endpoints authenticated by the owner-only per-boot presence token
/// (<c>~/.intochat/run/presence</c>) so <c>intochat --quit</c> can trigger a clean, journal-flushed shutdown.
/// </summary>
public static class RuntimeShutdownEndpoints
{
    public const string PresenceTokenEnvKey = "INTOCHAT_PRESENCE_TOKEN";
    public const string PresenceTokenFileConfigKey = "IntoChatOS:Security:PresenceTokenFile";
    public const string PresenceHeaderName = "X-Presence-Token";
    public const string CanonicalPresenceHeaderName = "X-IntoChat-Presence";
    private const int MinimumTokenLength = 32;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/runtime").AllowAnonymous();

        group.MapGet("/status", (HttpContext http, [FromServices] IConfiguration configuration) =>
        {
            if (!IsLoopbackRequest(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var expectedToken = ResolveExpectedPresenceToken(configuration);
            if (expectedToken is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!HasValidPresenceToken(http.Request, expectedToken))
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
            [FromServices] IConfiguration configuration,
            [FromServices] IHostApplicationLifetime lifetime,
            [FromServices] ILoggerFactory loggers) =>
        {
            if (!IsLoopbackRequest(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var expectedToken = ResolveExpectedPresenceToken(configuration);
            if (expectedToken is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!HasValidPresenceToken(http.Request, expectedToken))
            {
                return Results.Unauthorized();
            }

            var logger = loggers.CreateLogger("IntoChat.OS.Kernel.Lifecycle");
            logger.LogInformation("Graceful kernel shutdown requested via authenticated loopback presence token (PID {Pid}).", Environment.ProcessId);
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

    public static string? ResolveExpectedPresenceToken(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredToken = configuration[PresenceTokenEnvKey]?.Trim();
        if (!string.IsNullOrEmpty(configuredToken) && configuredToken.Length >= MinimumTokenLength)
        {
            return configuredToken;
        }

        var tokenFile = configuration[PresenceTokenFileConfigKey]?.Trim();
        if (!string.IsNullOrEmpty(tokenFile) && File.Exists(tokenFile))
        {
            try
            {
                var fileToken = File.ReadAllText(tokenFile, Encoding.UTF8).Trim();
                if (fileToken.Length >= MinimumTokenLength)
                {
                    return fileToken;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex.Message;
            }
        }

        return null;
    }

    public static bool HasValidPresenceToken(HttpRequest request, string expectedToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(expectedToken) || expectedToken.Length < MinimumTokenLength)
        {
            return false;
        }

        var candidate = ExtractCandidateToken(request);
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return candidateBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }

    private static string? ExtractCandidateToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(PresenceHeaderName, out var direct) && direct.Count == 1)
        {
            var token = direct[0]?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        if (request.Headers.TryGetValue(CanonicalPresenceHeaderName, out var canonical) && canonical.Count == 1)
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
