using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Form;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Kernel.Tests.E2E;

// Ruling 7, live: a secret entered through the IForm secure window (POST /secrets/{principalId}, then the form holds
// only its reference) never reaches Ino's chat surfaces, even while a turn is running, and a refused save leaves no
// trace in Ino's status. The fake model answers Ino slowly so the secret is set mid-turn.
[Collection("Router host")]
public sealed class InoSecretFacts(RouterHostFixture host) : BrainFact(host)
{
    private const string Secret = "fake-secret-value";
    private const string InoPrompt = "You are the user's assistant";
    private const string IntendPrompt = "Your only job is to rephrase it";
    private const string Ask = "connect my billing service";

    private static string ModelId => LLMModel.FindByMarker(typeof(IGpt56Luna))!.Id;

    private string InoKey => IApp.Key(WorkspaceId, "ino");
    private string MemoryKey => IApp.Key(WorkspaceId, "memory");

    [Fact(Timeout = 300_000)]
    public async Task ASecretSetThroughTheFormNeverReachesChatSurfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);

        var chat = Brain.Get<IChat>(InoKey + "/transcript");
        var before = (await chat.Read()).Selection?.Agent;
        await Send("new", "", ct);
        // "new" is handled asynchronously: send only once the new conversation is selected, and watch that agent,
        // or the message can land in (and the wait can watch) the previous conversation.
        var agentKey = (await TestWait.UntilAsync(async _ => (await chat.Read()).Selection,
            selection => selection is not null && selection.Agent != before && selection.Agent.Contains("/conversations/c-", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30), ct))!.Agent;
        await Send("send", Ask, ct);
        var agent = Brain.Get<IAgent>(agentKey);
        var running = await TestWait.UntilAsync(_ => agent.Read(), state => state.ActiveRun is not null, TimeSpan.FromSeconds(90), ct);
        var runId = running.ActiveRun!.RunId;

        // The secure window, mid-turn: the value goes to the principal's secret ring; the form gets the reference.
        var form = Brain.Get<IForm>(WorkspaceId + "/apps/forms/billing");
        await form.Define(new("Billing", [new("apiKey", "API key", FormFieldKind.Secret, Required: true)]));
        using var saved = await User.Http.PostAsJsonAsync($"/secrets/{User.PrincipalId}", new { name = "forms.billing.apiKey", label = "API key", value = Secret }, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var reference = (await saved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("reference").GetString();
        Assert.StartsWith("secret://", reference, StringComparison.Ordinal);
        using var evented = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/event",
            new { kind = "form", name = WorkspaceId + "/apps/forms/billing", action = "secret", field = "apiKey", value = reference }, ct);
        Assert.Equal(HttpStatusCode.OK, evented.StatusCode);
        Assert.True((await form.Read()).Fields.Single().SecretSet);

        // A refused save (another principal's ring) is refused without echoing the value, and Ino says nothing of it.
        using var refused = await User.Http.PostAsJsonAsync($"/secrets/someone-else-{Guid.NewGuid():N}", new { name = "forms.billing.apiKey", label = "API key", value = Secret }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain(Secret, await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        await TestWait.UntilAsync(_ => agent.Read(), state => state.ActiveRun is null && state.Runs.Any(run => run.RunId == runId),
            TimeSpan.FromSeconds(90), ct);
        var episode = await TestWait.UntilAsync(_ => Episodes(), rows => rows.Any(row => row.StartsWith(agentKey + "#" + runId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(90), ct);

        var status = (await Status().Read()).Markdown;
        var surfaces = new Dictionary<string, string>
        {
            ["composer draft"] = JsonSerializer.Serialize(await Brain.Get<IComposer>(InoKey + "/composer").Read()),
            ["transcript"] = JsonSerializer.Serialize(await chat.Read()),
            ["status"] = status,
            ["memory episode"] = string.Join("\n", episode),
            ["agent state"] = JsonSerializer.Serialize(await agent.Read()),
            ["agent transcript"] = JsonSerializer.Serialize(await agent.Transcript()),
        };
        foreach (var (surface, text) in surfaces)
        { Assert.False(text.Contains(Secret, StringComparison.Ordinal), surface + " holds the secret"); }
        Assert.DoesNotContain("secret://", status, StringComparison.Ordinal);
        Assert.DoesNotContain("403", status, StringComparison.Ordinal);
        // The model saw only the conversation: it was asked (so the check below is not vacuous), and no request it
        // received carries the value.
        Assert.NotEmpty(Host.Model.Requests(ModelId));
        Assert.DoesNotContain(Host.Model.Requests(ModelId), request => request.GetRawText().Contains(Secret, StringComparison.Ordinal));
    }

    private IText Status() => Brain.Get<IText>(InoKey + "/status");

    private async Task Send(string operation, string input, CancellationToken ct)
    {
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation, input }, ct);
        sent.EnsureSuccessStatusCode();
    }

    // Each episode row as "id summary", read as Memory (its tables answer only Memory).
    private async Task<string[]> Episodes()
    {
        using var app = User.AsApp(MemoryKey);
        try
        {
            var page = await Brain.Get<ITable>(MemoryKey + "/episodes").Page(0, 200);
            return [.. page.Select(cells => Cell(cells, "id") + " " + string.Join(" ", cells.Select(cell => cell.Json)))];
        }
        catch (Exception) { return []; } // not defined yet
    }

    private static string Cell(TableValue[] cells, string column)
    {
        var json = cells.FirstOrDefault(cell => cell.Column == column)?.Json;
        if (json is null) { return ""; }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : "";
    }

    // Jev is unsure, Memory's intend writes a low-confidence brief, Ino answers after a few seconds (so the secret is set
    // while the turn runs), and Memory's capture notes the turn. Restored when disposed.
    private IDisposable Answering()
    {
        var fallback = Host.Model.ChatFallback;
        var intercept = Host.Model.Intercept;
        var route = Host.Model.SystemOneOverride;
        Host.Model.Clear(ModelId);
        Host.Model.SystemOneOverride = request => RouterHostFixture.Choose(request, "router.ask", "ask", 0.2);
        Host.Model.ChatFallback = request =>
        {
            var text = RouterHostFixture.Text(request);
            if (text.Contains(IntendPrompt, StringComparison.Ordinal))
            {
                return FakeReply.Text(JsonSerializer.Serialize(new
                {
                    brief = new { goal = "Reply to the user's message", inputs = Array.Empty<string>(), constraints = Array.Empty<string>(), acceptance = new[] { "The user has a reply" } },
                    confidence = 0.3,
                    continues = (string?)null,
                }));
            }
            if (text.Contains(InoPrompt, StringComparison.Ordinal))
            { return FakeReply.Delayed(TimeSpan.FromSeconds(8), FakeReply.Text("Open the secure window to add the key.")); }
            return fallback?.Invoke(request);
        };
        Host.Model.Intercept = request =>
        {
            var text = RouterHostFixture.Text(request);
            return text.Contains(RouterHostFixture.CapturePrompt, StringComparison.Ordinal)
                ? FakeReply.Text(JsonSerializer.Serialize(new { summary = "- The user asked to connect a billing service.\n- The assistant pointed to the secure window.", importance = 3 }))
                : text.Contains(RouterHostFixture.ConsolidatePrompt, StringComparison.Ordinal)
                    ? FakeReply.Text(JsonSerializer.Serialize(new { items = Array.Empty<object>() }))
                    : null;
        };
        return new Restore(() => { Host.Model.ChatFallback = fallback; Host.Model.Intercept = intercept; Host.Model.SystemOneOverride = route; });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
