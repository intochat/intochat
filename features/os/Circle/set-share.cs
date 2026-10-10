using System.Globalization;
using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "set-share": the compute share of a circle, 0 to 25 percent. The slider posts a bare number.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "set-share", brain.Stopping))
{
    try
    {
        var (name, rest) = await CircleView.Split(brain, request.Input);
        if (name.Length == 0) { throw new InvalidOperationException("Select or name a circle first."); }
        if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        { throw new ArgumentException("The compute share is a number from 0 to " + ICircle.MaxComputeSharePercent + "."); }
        var percent = (int)Math.Round(value);
        await CircleView.Circle(brain, name).SetComputeShare(Math.Clamp(percent, 0, ICircle.MaxComputeSharePercent));
        await CircleView.Status(brain, "");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
