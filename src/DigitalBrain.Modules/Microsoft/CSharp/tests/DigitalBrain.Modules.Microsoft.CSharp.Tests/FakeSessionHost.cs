using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// Stands in for the sandbox host's session API: a start records the session, a stop forgets it, and a
// test can lose every session as a replaced container would.
internal sealed class FakeSessionHost : HttpMessageHandler
{
    public const string Url = "http://sandbox.test/";

    public ConcurrentDictionary<string, JsonNode> Sessions { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<(string Method, string Path, JsonNode? Body)> Requests { get; } = new();
    // Nothing listens: every request fails to connect, as when the sandbox container is not up yet.
    public bool Absent { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Absent) { throw new HttpRequestException("No connection could be made because the target machine actively refused it."); }
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var body = text.Length == 0 ? null : JsonNode.Parse(text);
        Requests.Enqueue((request.Method.Method, path, body));
        return path.Split('/') switch
        {
            ["sessions", var id] when request.Method == HttpMethod.Post => Started(id, body!),
            ["sessions", var id] when Sessions.ContainsKey(id) => new(HttpStatusCode.OK),
            ["sessions", var id, "stop"] when Sessions.TryRemove(id, out _) => new(HttpStatusCode.OK),
            _ => new(HttpStatusCode.NotFound),
        };
    }

    public static bool IsStart((string Method, string Path, JsonNode? Body) request)
        => request.Method == "POST" && request.Path.Split('/') is ["sessions", _];

    private HttpResponseMessage Started(string id, JsonNode body)
    {
        Sessions[id] = body;
        return new(HttpStatusCode.Accepted);
    }
}
