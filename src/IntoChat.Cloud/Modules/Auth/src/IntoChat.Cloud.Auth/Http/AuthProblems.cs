using System.Text.Json;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Auth.Http;

// Uniform error bodies ({ code, ... }) and the bounded JSON body reader every Auth route uses. Error bodies never echo input.
internal static class AuthProblems
{
    public const int MaxBodyBytes = 4096;
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string EmailInvalid = "EMAIL_INVALID";
    public const string PasswordInvalid = "PASSWORD_INVALID";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IResult Code(int status, string code) => Results.Json(new { code }, statusCode: status);

    public static IResult BadRequest(string code) => Code(StatusCodes.Status400BadRequest, code);

    public static IResult RateLimited(DateTimeOffset? retryAt) =>
        Results.Json(new { code = AuthErrors.EmailCodeRateLimited, retryAt }, statusCode: StatusCodes.Status429TooManyRequests);

    public static IResult Accepted(CodeIssue issue) =>
        Results.Json(new { codeId = issue.CodeId, resendAvailableAt = issue.ResendAvailableAt, expiresAt = issue.ExpiresAt }, statusCode: StatusCodes.Status202Accepted);

    public static IResult Issued(CodeIssue issue) =>
        issue.Outcome == CodeIssueOutcome.RateLimited ? RateLimited(issue.RetryAt) : Accepted(issue);

    // A failed code verification: the same shapes for every subject, real or decoy.
    public static IResult Verdict(CodeVerdict verdict) => verdict.Outcome switch
    {
        CodeOutcome.Invalid => Results.Json(new { code = AuthErrors.EmailCodeInvalid, attemptsLeft = verdict.AttemptsLeft }, statusCode: StatusCodes.Status400BadRequest),
        CodeOutcome.Expired => Reissued(AuthErrors.EmailCodeExpired, verdict.Reissued!),
        CodeOutcome.Locked => Reissued(AuthErrors.EmailCodeLocked, verdict.Reissued!),
        CodeOutcome.RateLimited => RateLimited(verdict.RetryAt),
        _ => NotFound(),
    };

    public static IResult NotFound() => Code(StatusCodes.Status404NotFound, AuthErrors.EmailCodeNotFound);

    public static async Task<(T? Body, IResult? Failure)> ReadAsync<T>(HttpRequest request) where T : class
    {
        if (!request.HasJsonContentType())
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaxBodyBytes)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Json) is { } body ? (body, null) : (null, BadRequest(RequestInvalid));
        }
        catch (JsonException)
        {
            return (null, BadRequest(RequestInvalid));
        }
    }

    private static IResult Reissued(string code, CodeIssue issue) =>
        Results.Json(new { code, codeId = issue.CodeId, resendAvailableAt = issue.ResendAvailableAt, expiresAt = issue.ExpiresAt }, statusCode: StatusCodes.Status400BadRequest);
}
