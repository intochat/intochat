using System.Text.Json;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Spi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Organisations;

internal sealed class InviteHttpSurface : IModuleHttpSurface
{
    internal const string PreviewPath = "/organisation/invites/preview";
    internal const string AcceptPath = "/organisation/invites/accept";
    internal const string DeclinePath = "/organisation/invites/decline";
    internal const string NotFoundCode = "INVITE_NOT_FOUND";
    internal const int MaxBodyBytes = 1024;
    private const string NotFoundBody = "{\"code\":\"INVITE_NOT_FOUND\"}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewPath, PreviewAsync).AllowAnonymous();
        endpoints.MapPost(AcceptPath, AcceptAsync);
        endpoints.MapPost(DeclinePath, DeclineAsync);
    }

    private static Task<IResult> PreviewAsync(HttpRequest request, INeuronProxies proxies, IOrganisationHost host, ILoggerFactory loggers) =>
        RunAsync(request, proxies, host, loggers, "preview", async (invites, token) =>
        {
            var preview = await host.InModuleCall(() => invites.PreviewByHash(InviteTokens.Hash(token)));
            return preview is null
                ? NotFound()
                : Results.Json(new { organisationName = preview.OrganisationName, inviterName = preview.InviterName, role = preview.Role.ToString(), expiresAt = preview.ExpiresAt });
        });

    private static Task<IResult> AcceptAsync(HttpRequest request, INeuronProxies proxies, IOrganisationHost host, ILoggerFactory loggers) =>
        RunAsync(request, proxies, host, loggers, "accept", async (invites, token) =>
        {
            var accepted = await invites.Accept(token);
            return Results.Json(new { organisationId = accepted.OrganisationId, role = accepted.Role.ToString() });
        });

    private static Task<IResult> DeclineAsync(HttpRequest request, INeuronProxies proxies, IOrganisationHost host, ILoggerFactory loggers) =>
        RunAsync(request, proxies, host, loggers, "decline", async (invites, token) =>
        {
            await invites.Decline(token);
            return Results.NoContent();
        });

    private static async Task<IResult> RunAsync(
        HttpRequest request,
        INeuronProxies proxies,
        IOrganisationHost host,
        ILoggerFactory loggers,
        string operation,
        Func<IOrganisationInvites, string, Task<IResult>> run)
    {
        var read = await ReadTokenAsync(request);
        if (read.Failure is { } failure) { return failure; }
        var tokenHash = InviteTokens.Hash(read.Token!);
        var invites = await LocateAsync(proxies, host, tokenHash);
        if (invites is null)
        {
            Log(loggers, operation, NotFoundCode, tokenHash);
            return NotFound();
        }

        try
        {
            var result = await run(invites, read.Token!);
            Log(loggers, operation, result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status404NotFound } ? NotFoundCode : "OK", tokenHash);
            return result;
        }
        catch (OrganisationException error)
        {
            Log(loggers, operation, error.Code, tokenHash);
            return Map(error.Code);
        }
        catch (UnauthorizedAccessException)
        {
            Log(loggers, operation, "FORBIDDEN", tokenHash);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IOrganisationInvites?> LocateAsync(INeuronProxies proxies, IOrganisationHost host, string tokenHash)
    {
        var reference = await host.InModuleCall(() => proxies.Get<IInviteToken>(OrgIds.Of(IInviteToken.Type, IInviteToken.Key(tokenHash))).Resolve());
        return reference is null ? null : proxies.Get<IOrganisationInvites>(OrgIds.Of(IOrganisationInvites.Type, IOrganisationInvites.Key(reference.OrgBrain)));
    }

    private static IResult Map(string code) => code switch
    {
        NotFoundCode or "ORGANISATION_NOT_FOUND" => NotFound(),
        "MEMBERSHIP_CONFLICT" or "INVITE_EMAIL_MISMATCH" => Results.Json(new { code }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { code }, statusCode: StatusCodes.Status400BadRequest),
    };

    private static IResult NotFound() => Results.Text(NotFoundBody, "application/json", statusCode: StatusCodes.Status404NotFound);

    private static void Log(ILoggerFactory loggers, string operation, string outcome, string tokenHash) =>
        loggers.CreateLogger("IntoChat.Cloud.Organisations.Invites").LogDebug("Invite {Operation} finished with {Outcome} for token hash {TokenHashPrefix}", operation, outcome, tokenHash[..Math.Min(8, tokenHash.Length)]);

    private static async Task<(string? Token, IResult? Failure)> ReadTokenAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType()) { return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType)); }
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length));
            if (read == 0) { break; }
            length += read;
        }
        if (length > MaxBodyBytes) { return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge)); }
        var token = TryParseToken(buffer.AsSpan(0, length));
        return InviteTokens.IsWellFormed(token) ? (token, null) : (null, NotFound());
    }

    private static string? TryParseToken(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("token", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
