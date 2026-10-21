using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Sdk.Http;

// Uniform error bodies ({ code, ... }) and a bounded JSON body reader. Error bodies never echo input.
public static class HttpProblems
{
    public const int MaxBodyBytes = 4096;
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string RateLimitedCode = "RATE_LIMITED";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IResult Code(int status, string code) => Results.Json(new { code }, statusCode: status);

    public static IResult BadRequest(string code) => Code(StatusCodes.Status400BadRequest, code);

    public static IResult RateLimited(DateTimeOffset? retryAt) =>
        Results.Json(new { code = RateLimitedCode, retryAt }, statusCode: StatusCodes.Status429TooManyRequests);

    public static async Task<(T? Body, IResult? Failure)> ReadAsync<T>(HttpRequest request, int maxBodyBytes = MaxBodyBytes) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.HasJsonContentType())
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        var buffer = new byte[maxBodyBytes + 1];
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

        if (length > maxBodyBytes)
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
}
