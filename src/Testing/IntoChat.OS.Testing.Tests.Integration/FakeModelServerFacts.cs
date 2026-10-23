using System.Net;
using System.Text.Json;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Testing.Tests.Integration;

public sealed class FakeModelServerFacts
{
    [Fact]
    public async Task AnExhaustedScriptRejectsRequestsEvenWithAFallbackConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = model.Endpoint };
        model.ChatFallback = _ => FakeReply.Text("Fallback reply.");
        model.Script("scripted", FakeReply.Text("Scripted reply."));

        using var first = await http.PostAsJsonAsync("chat/completions", Request("scripted"), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Scripted reply.", await Content(first, ct));
        using var exhausted = await http.PostAsJsonAsync("chat/completions", Request("scripted"), ct);
        Assert.Equal(HttpStatusCode.InternalServerError, exhausted.StatusCode);
        Assert.Contains("ran out of replies after 1", await exhausted.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClearedScriptNeedsAnExplicitReplyWhenFallbackIsEnabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = model.Endpoint };
        model.FallbackWhenScriptExhausted = true;
        model.ChatFallback = _ => null;

        using var fresh = await http.PostAsJsonAsync("chat/completions", Request("scripted"), ct);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal("You said: Synthetic request.", await Content(fresh, ct));

        model.Script("scripted", FakeReply.Text("Unused reply."));
        model.Clear("scripted");
        using var cleared = await http.PostAsJsonAsync("chat/completions", Request("scripted"), ct);
        Assert.Equal(HttpStatusCode.InternalServerError, cleared.StatusCode);

        model.ChatFallback = _ => FakeReply.Text("Explicit reply.");
        using var answered = await http.PostAsJsonAsync("chat/completions", Request("scripted"), ct);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal("Explicit reply.", await Content(answered, ct));
    }

    private static object Request(string model)
        => new { model, messages = new[] { new { role = "user", content = "Synthetic request." } } };

    private static async Task<string?> Content(HttpResponseMessage response, CancellationToken ct)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }
}
