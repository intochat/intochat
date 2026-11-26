using System.Text;
using System.Text.Json;
using DigitalBrain.Modules.Validation;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class HttpProblemsFacts
{
    private sealed record SampleBody(string Name);

    [Fact]
    public async Task ReadAsyncRejectsABodyOverTheLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', HttpProblems.MaxBodyBytes + 1)));

        var (body, failure) = await HttpProblems.ReadAsync<SampleBody>(context.Request);

        Assert.Null(body);
        Assert.NotNull(failure);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);
        var payload = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(failure).Value);
        Assert.Contains(HttpProblems.RequestInvalid, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncDeserializesAValidBodyWithinTheLimit()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("{\"name\":\"ada\"}"u8.ToArray());

        var (body, failure) = await HttpProblems.ReadAsync<SampleBody>(context.Request);

        Assert.Null(failure);
        Assert.Equal(new SampleBody("ada"), body);
    }

    [Fact]
    public async Task ErrorBodiesKeepTheirWireShape()
    {
        Assert.Equal("{\"code\":\"NOPE\"}", await WriteAsync(HttpProblems.Code(StatusCodes.Status409Conflict, "NOPE")));
        Assert.Equal("{\"code\":\"RATE_LIMITED\",\"retryAt\":null}", await WriteAsync(HttpProblems.RateLimited(null)));
        Assert.Equal(
            "{\"code\":\"REQUEST_INVALID\",\"issues\":[{\"path\":\"name\",\"code\":\"REQUIRED\",\"message\":\"Name is required.\"}]}",
            await WriteAsync(HttpProblems.Validation([new ValidationIssue("name", "REQUIRED", "Name is required.")])));
    }

    private static async Task<string> WriteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        return Encoding.UTF8.GetString(body.ToArray());
    }
}
