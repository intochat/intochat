using System.Net;
using System.Text.Json;
using IntoChat.OS.Cli;

namespace IntoChat.OS.Tests.Unit.Cli;

// approve and deny post the person's answer to the brain's confirmation route, never to the UI event route.
public sealed class CliAnswerFacts
{
    [Theory]
    [InlineData("approve", "plan 7", true, "/brains/home/confirmations/plan%207")]
    [InlineData("deny", "plan 7", false, "/brains/home/confirmations/plan%207")]
    [InlineData("approve", "cb-codeblocks/run 1", true, "/brains/home/confirmations/cb-codeblocks/run%201")]
    public async Task AnAnswerPostsToTheConfirmationRouteOfThePlan(string verb, string plan, bool approve, string path)
    {
        var handler = new RecordingHandler();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await CliCommands.Run([verb, plan, "--brain", "home"], output, error, _ => null, handler);

        Assert.Equal(0, code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(path, request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(approve, body.RootElement.GetProperty("approve").GetBoolean());
        Assert.Equal(["approve"], body.RootElement.EnumerateObject().Select(member => member.Name));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
