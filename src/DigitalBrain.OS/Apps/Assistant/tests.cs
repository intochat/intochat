#:project /brain/src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/AI/DigitalBrain.Modules.AI.Contracts/DigitalBrain.Modules.AI.Contracts.csproj
using System.Text.Json;
using DigitalBrain.AI.Scripted;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Surface;

// Scripted models play the provider; each scenario starts its own conversation so turns never mix.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var failures = 0;
var round = 0;

async Task<IScriptedLLM> Scripted(params string[] replies)
{
    var key = appKey + "/scenario-" + ++round + "/model";
    await app.Configure(new Dictionary<string, string> { ["Model"] = IScriptedLLM.ModelPrefix + key }, new Dictionary<string, string>());
    var model = brain.Get<IScriptedLLM>(key);
    await model.Script(replies);
    await app.Ask("new", "");
    return model;
}

async Task<JsonElement> SettledTranscript(int turns)
{
    var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
    while (true)
    {
        var transcript = JsonDocument.Parse(await app.Ask("transcript", "")).RootElement;
        if (transcript.GetArrayLength() == turns && transcript.EnumerateArray().All(turn => turn.GetProperty("status").GetString() != "running"))
        { return transcript; }
        if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException("The turn did not settle: " + transcript.GetRawText()); }
        await Task.Delay(200);
    }
}

await Scenario("Opening the assistant composes its surface", async () =>
{
    var window = JsonDocument.Parse(await app.Ask("open", "")).RootElement;
    if (window.GetProperty("title").GetString() != "Assistant" || window.GetProperty("docked").GetBoolean())
    { throw new InvalidOperationException("The window contract is wrong: " + window.GetRawText()); }
    var surface = await brain.Get<ISurface>(window.GetProperty("surface").GetString()!).Read();
    if (surface.Definition.Title != "Assistant" || surface.Definition.Children.Length == 0)
    { throw new InvalidOperationException("The assistant surface is not composed."); }
});

await Scenario("Opening docked answers the surface without a window", async () =>
{
    var docked = JsonDocument.Parse(await app.Ask("open", "docked")).RootElement;
    var windowed = JsonDocument.Parse(await app.Ask("open", "")).RootElement;
    if (docked.GetProperty("id").GetString() != "assistant-docked" || !docked.GetProperty("docked").GetBoolean()
        || docked.GetProperty("surface").GetString() != windowed.GetProperty("surface").GetString())
    { throw new InvalidOperationException("Docked open answered " + docked.GetRawText()); }
});

await Scenario("A message is answered in the transcript", async () =>
{
    await Scripted("Hi! How can I help?");
    await app.Ask("send", "hello");
    var turn = (await SettledTranscript(1))[0];
    if (turn.GetProperty("user").GetString() != "hello" || turn.GetProperty("assistant").GetString() != "Hi! How can I help?"
        || turn.GetProperty("status").GetString() != "done")
    { throw new InvalidOperationException("The turn was " + turn.GetRawText()); }
});

await Scenario("A plain answer leaves nothing behind but its turn", async () =>
{
    await Scripted("Just words.");
    await app.Ask("send", "say something");
    var turn = (await SettledTranscript(1))[0];
    var step = await brain.Get<ICard>(appKey + "/turn-" + turn.GetProperty("runId").GetString() + "-step-1").Read();
    if (step.Title.Length > 0) { throw new InvalidOperationException("A text piece was stored as its own card: " + step.Title); }
});

await Scenario("The tools a turn uses appear in the transcript", async () =>
{
    await Scripted("""{"tool":"list_neurons","arguments":{}}""", "You have some neurons.");
    await app.Ask("send", "what do I have?");
    var turn = (await SettledTranscript(1))[0];
    var kinds = turn.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("kind").GetString()).ToArray();
    if (!kinds.Contains("tool-call") || !kinds.Contains("tool-result")
        || turn.GetProperty("steps").EnumerateArray().First().GetProperty("tool").GetString() != "list_neurons")
    { throw new InvalidOperationException("The steps were " + turn.GetProperty("steps").GetRawText()); }
    if (turn.GetProperty("assistant").GetString() != "You have some neurons.") { throw new InvalidOperationException("No answer after the tool."); }
});

await Scenario("A failed turn says so and the next message still works", async () =>
{
    var model = await Scripted();
    await app.Ask("send", "first");
    var failed = (await SettledTranscript(1))[0];
    if (failed.GetProperty("status").GetString() != "failed") { throw new InvalidOperationException("The turn was " + failed.GetRawText()); }
    await model.Script(["Second answer."]);
    await app.Ask("send", "second");
    var second = (await SettledTranscript(2))[1];
    if (second.GetProperty("assistant").GetString() != "Second answer.") { throw new InvalidOperationException("The follow-up was " + second.GetRawText()); }
});

return failures == 0 ? 0 : 1;

async Task Scenario(string name, Func<Task> run)
{
    try
    {
        await run();
        Console.WriteLine($"dbtest:pass {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"dbtest:fail {name}\t{error.Message.ReplaceLineEndings(" ")}");
    }
}
