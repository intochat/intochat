using System.Globalization;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "set-share": the compute share of a circle, 0 to 25 percent. The slider posts a bare number.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "set-share") { continue; }
    try
    {
        var (name, rest) = await CircleView.Split(brain, request.Input);
        if (name.Length == 0) { throw new InvalidOperationException("Select or name a circle first."); }
        var percent = (int)Math.Round(double.Parse(rest, CultureInfo.InvariantCulture));
        await CircleView.Circle(brain, name).SetComputeShare(Math.Clamp(percent, 0, ICircle.MaxComputeSharePercent));
        await CircleView.Status(brain, "");
        await CircleView.Project(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
