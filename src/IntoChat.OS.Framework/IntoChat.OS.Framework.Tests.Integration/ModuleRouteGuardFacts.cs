using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Connections;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ModuleRouteGuardFacts
{
    [Theory]
    [InlineData(typeof(FileNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(IOException), StatusCodes.Status503ServiceUnavailable)]
    [InlineData(typeof(KeyNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(UnauthorizedAccessException), StatusCodes.Status403Forbidden)]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status409Conflict)]
    public async Task EveryMappedExceptionBecomesItsStatus(Type exception, int status)
    {
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());
        var result = await ModuleRouteGuard.Guard(context, _ => throw (Exception)Activator.CreateInstance(exception, "boom")!);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task ARevisionConflictAnswers409WithTheCurrentRevision()
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        var result = await ModuleRouteGuard.Guard(new DefaultEndpointFilterInvocationContext(http), _ => throw new RevisionConflictException("stale", 4));
        await ((IResult)result!).ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, body.RootElement.GetProperty("currentRevision").GetInt64());
    }
}
