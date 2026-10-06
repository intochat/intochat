#:project /brain/src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
using DigitalBrain.Apps;

// The host verifies a scratch copy of the app and names it in the "App" setting; the tests
// drive that copy through its own operations and report one dbtest line per spec scenario.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(brain.Setting("App")!);
var failures = 0;

await Scenario("Words are separated by spaces", async () =>
{
    var answer = await Ask("ask", "the quick brown fox");
    if (answer != "4 words") { throw new InvalidOperationException($"The answer was \"{answer}\"."); }
});

await Scenario("Extra spaces do not count as words", async () =>
{
    var answer = await Ask("ask", "  hello    world  ");
    if (answer != "2 words") { throw new InvalidOperationException($"The answer was \"{answer}\"."); }
});

await Scenario("One word is singular", async () =>
{
    var answer = await Ask("ask", "hello");
    if (answer != "1 word") { throw new InvalidOperationException($"The answer was \"{answer}\"."); }
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

async Task<string> Ask(string operation, string input)
{
    var invocation = await app.Invoke(new InvokeApp(Guid.NewGuid(), operation, input));
    var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    while (invocation.Status == InvocationStatus.Pending)
    {
        if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException("The app did not answer within 5 minutes."); }
        await Task.Delay(200);
        invocation = await app.ReadInvocation(invocation.Id);
    }
    if (invocation.Status == InvocationStatus.Failed) { throw new InvalidOperationException(invocation.Error ?? "The app failed."); }
    return invocation.Output ?? "";
}
