using System.Text;
using System.Text.Json;
using IntoChat.OS.Framework.Modules.Http;
using IntoChat.OS.Framework.Modules.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Tests.Unit;

public sealed class HttpValidationFacts
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/problem+json")]
    public async Task ValidJsonInvokesTheValidatorOnce(string mediaType)
    {
        var context = Context("{\"name\":\"accepted\"}", mediaType);
        var validator = new RequestValidator();
        var (body, failure) = await HttpProblems.ReadValidatedAsync(context.Request, validator, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal("accepted", body!.Name);
        Assert.Equal(1, validator.Calls);
    }

    [Theory]
    [InlineData("", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("{", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task ParseAndMediaFailuresNeverInvokeValidation(string content, string mediaType, int expected)
    {
        var validator = new RequestValidator();
        var (_, failure) = await HttpProblems.ReadValidatedAsync(Context(content, mediaType).Request, validator, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);
        Assert.Equal(0, validator.Calls);
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(12, true)]
    public async Task ChunkedBodyHonorsTheExactByteLimitWithoutContentLength(int limit, bool rejected)
    {
        var context = Context("{\"name\":\"é\"}"); // 13 UTF-8 bytes.
        context.Request.Body = new ChunkedStream(Encoding.UTF8.GetBytes("{\"name\":\"é\"}"));
        Assert.Null(context.Request.ContentLength);
        var validator = new RequestValidator();
        var (body, failure) = await HttpProblems.ReadValidatedAsync(context.Request, validator, limit, TestContext.Current.CancellationToken);
        if (rejected)
        {
            Assert.Null(body);
            Assert.Equal(413, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);
            Assert.Equal(0, validator.Calls);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal("é", body!.Name);
            Assert.Equal(1, validator.Calls);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task InvalidConfiguredLimitsFailBeforeReading(int limit)
    {
        var context = Context("{}");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HttpProblems.ReadAsync<RequestBody>(context.Request, limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HttpProblems.ReadValidatedAsync(context.Request, new RequestValidator(), limit, TestContext.Current.CancellationToken));
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherCancellationSourceInterruptsAnActiveRead(bool abortRequest)
    {
        using var cancellation = new CancellationTokenSource();
        var context = Context("{}");
        var stream = new PendingStream();
        context.Request.Body = stream;
        context.RequestAborted = abortRequest ? cancellation.Token : TestContext.Current.CancellationToken;
        var validator = new RequestValidator();
        var pending = HttpProblems.ReadValidatedAsync(context.Request, validator, cancellationToken: abortRequest ? TestContext.Current.CancellationToken : cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task InvalidInputReturnsSafeStructuredIssues()
    {
        var context = Context("{\"name\":\"secret-sentinel\"}");
        var (body, failure) = await HttpProblems.ReadValidatedAsync(context.Request, new RequestValidator { Reject = true }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(body);
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        await failure!.ExecuteAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        var json = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.DoesNotContain("secret-sentinel", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("REQUEST_INVALID", parsed.RootElement.GetProperty("code").GetString());
        var issue = Assert.Single(parsed.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal("name", issue.GetProperty("path").GetString());
        Assert.Equal("name", issue.GetProperty("code").GetString());
        Assert.Equal("Choose an allowed name.", issue.GetProperty("message").GetString());
    }

    private static DefaultHttpContext Context(string content, string mediaType = "application/json")
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = mediaType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return context;
    }

    public sealed record RequestBody(string? Name);

    private sealed class RequestValidator : IModuleValidator<RequestBody>
    {
        public int Calls { get; private set; }
        public bool Reject { get; init; }
        public ValidationIssue[] Validate(RequestBody value)
        {
            Calls++;
            return Reject ? [new("name", "name", "Choose an allowed name.")] : [];
        }
    }

    private sealed class ChunkedStream(byte[] content) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }

    private sealed class PendingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
