using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// The signed-in person's own profile and avatar. Every route reads the stamped caller; none takes a principal id.
internal static class AccountEndpoints
{
    public const int AvatarMaxBytes = 5 * 1024 * 1024;
    internal const int DisplayNameMax = 100;
    internal static readonly string[] Languages = ["en", "ru"];
    internal static readonly string[] Themes = ["light", "dark", "system"];
    internal static readonly string[] TimeZones = ["UTC", "Europe/London", "Europe/Berlin", "Europe/Kyiv", "America/New_York", "America/Los_Angeles", "Asia/Tokyo"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/profile", GetProfileAsync);
        endpoints.MapPut("/auth/profile", PutProfileAsync);
        endpoints.MapGet("/auth/avatar", GetAvatarAsync);
        endpoints.MapPut("/auth/avatar", PutAvatarAsync);
        endpoints.MapDelete("/auth/avatar", DeleteAvatarAsync);
    }

    private static async Task<IResult> GetProfileAsync(HttpContext http, AuthCalls calls)
    {
        var caller = AuthCaller.Require(http);
        var profile = await calls.Profile(caller.PrincipalId, p => p.Get());
        var flags = await calls.Credential(caller.PrincipalId, credential => credential.GetFlags());
        var emailHash = await calls.Credential(caller.PrincipalId, credential => credential.GetEmailHash());
        var entry = emailHash is null ? null : await calls.EmailIndex(emailHash, index => index.Get());
        var email = entry?.Email ?? string.Empty;
        var displayName = profile.DisplayName ?? entry?.PendingDisplayName ?? (email.Contains('@') ? email[..email.IndexOf('@')] : email);
        return Results.Json(new
        {
            displayName,
            email,
            language = profile.Language,
            theme = profile.Theme,
            timeZone = profile.TimeZone,
            hasPassword = flags.HasPassword,
            hasTotp = flags.HasTotp,
            hasAvatar = profile.HasAvatar,
        });
    }

    private static async Task<IResult> PutProfileAsync(HttpContext http, AuthCalls calls)
    {
        var caller = AuthCaller.Require(http);
        var (body, failure) = await AuthProblems.ReadAsync<ProfileRequest>(http.Request);
        if (failure is not null) { return failure; }
        var name = body!.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > DisplayNameMax || name.Any(char.IsControl)
            || !Languages.Contains(body.Language) || !Themes.Contains(body.Theme) || !TimeZones.Contains(body.TimeZone))
        {
            return AuthProblems.BadRequest(AuthProblems.RequestInvalid);
        }
        await calls.Profile(caller.PrincipalId, profile => profile.Update(new ProfileChange(name, body.Language!, body.Theme!, body.TimeZone!)));
        return Results.NoContent();
    }

    private static async Task<IResult> GetAvatarAsync(HttpContext http, AuthCalls calls)
    {
        var caller = AuthCaller.Require(http);
        if (await calls.Profile(caller.PrincipalId, profile => profile.GetAvatar()) is not { } avatar)
        {
            return AuthProblems.Code(StatusCodes.Status404NotFound, "NOT_FOUND");
        }
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return Results.Bytes(avatar.Bytes, avatar.ContentType);
    }

    private static async Task<IResult> PutAvatarAsync(HttpContext http, AuthCalls calls)
    {
        var caller = AuthCaller.Require(http);
        var request = http.Request;
        if (request.ContentLength > AvatarMaxBytes) { return TooLarge(); }
        var declared = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (declared is not (AvatarImages.Png or AvatarImages.Jpeg)) { return AuthProblems.BadRequest(AvatarInvalid); }

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > AvatarMaxBytes) { return TooLarge(); }
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (AvatarImages.Detect(bytes) is not { } detected || detected != declared) { return AuthProblems.BadRequest(AvatarInvalid); }
        await calls.Profile(caller.PrincipalId, profile => profile.SetAvatar(detected, bytes));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAvatarAsync(HttpContext http, AuthCalls calls)
    {
        var caller = AuthCaller.Require(http);
        await calls.Profile(caller.PrincipalId, profile => profile.RemoveAvatar());
        return Results.NoContent();
    }

    private static IResult TooLarge() => Results.Json(new { code = AvatarTooLarge }, statusCode: StatusCodes.Status413PayloadTooLarge);

    internal const string AvatarInvalid = "AVATAR_INVALID";
    internal const string AvatarTooLarge = "AVATAR_TOO_LARGE";

    private sealed record ProfileRequest(string? DisplayName, string? Language, string? Theme, string? TimeZone);
}
