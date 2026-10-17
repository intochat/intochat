using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Text;
using ITimer = DigitalBrain.Time.Timers.ITimer;

// "ask": the brain's router has Jev pick a capability and method, a language model fills the key and
// arguments, and the call runs until Jev says the request is done. The result neuron names each method
// that ran, or says the request was not clear enough to route when Jev chose nothing above its gate.
// A started timer joins the router's timer list and is shown in the router's main layout; showing it never
// replaces the routed answer. Handled request ids are neuron state, so a redelivered request is not
// carried out twice.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Jev.Key(brain));
var result = brain.Get<IText>(Jev.Name(brain, "result"));
var handled = brain.Get<IText>(Jev.Name(brain, "handled"));
await foreach (var request in brain.On(app, "ask", brain.Stopping))
{
    var seen = (await handled.Read()).Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var id = request.RequestId.ToString("N");
    if (seen.Contains(id)) { continue; }
    var input = request.Input.Trim();
    try
    {
        if (input.Length is < 1 or > 32000) { await result.Set("A request has 1-32000 characters."); }
        else { await Ask(input, id); }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await result.Set(Jev.Clip(error.Message)); }
    await handled.Set(string.Join('\n', seen.TakeLast(31).Append(id)));
}

// Each request is achieved on its own router neuron. A router shows Jev what it routed before, and a
// request it already carried out reads to Jev as a goal that is complete, so a repeated request on a
// shared router would end with no step.
async Task Ask(string input, string id)
{
    await result.Set("Working…");
    var router = brain.Get<IRouter>(Jev.Name(brain, "runs/" + id));
    var requested = DateTimeOffset.UtcNow;
    var run = await router.Achieve(input, await Jev.Capabilities(brain, brain.Stopping), brain.Stopping);
    var answered = DateTimeOffset.UtcNow;
    if (run.Steps.Length == 0)
    {
        await result.Set(Jev.Unclear);
        return;
    }
    var summary = string.Join("\n", run.Steps.Select(step => "• " + Jev.Short(step.Contract) + "." + step.Method
        + (step.Key is null ? "" : " on `" + step.Key + "`")
        + (step.Error is null ? "" : " (failed: " + Jev.Clip(step.Error, 300) + ")")));
    var last = run.Steps[^1];
    var answer = last.Error is null && run.Answer is { Length: > 0 } shown ? summary + "\n\n" + Jev.Clip(shown) : summary;
    await result.Set(answer);
    var timers = run.Steps.Select((step, index) => (Step: step, Index: index))
        .Where(item => item.Step.Contract == typeof(ITimer).FullName && item.Step.Method == nameof(ITimer.Start)
            && item.Step.Error is null && item.Step.Key is not null)
        .ToArray();
    if (timers.Length == 0) { return; }
    try
    {
        foreach (var (step, index) in timers)
        {
            var (due, period) = Timers.Durations(step.Arguments);
            await Record(new Timers.Started(id + "-" + index, step.Key!, requested, answered, due, period));
        }
        await Timers.Show(brain);
        await Jev.Show(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await result.Set(answer + "\n\nThe router could not be shown: " + Jev.Clip(error.Message, 300)); }
}

// The timers neuron lists the timers this router started; a redelivered request adds no second line.
async Task Record(Timers.Started timer)
{
    var ledger = brain.Get<IText>(Jev.Name(brain, "timers"));
    var lines = Timers.Lines((await ledger.Read()).Markdown);
    if (lines.Any(line => line.StartsWith(timer.Id + "\t", StringComparison.Ordinal))) { return; }
    await ledger.Set(string.Join('\n', lines.TakeLast(Timers.Kept - 1).Append(Timers.Line(timer))));
}
