using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.FxMonitor;

[Signal("fx.polled")]
public sealed record FxPolled(double EurUsd) : Signal;

[State("fx.rates.monitor")]
public sealed record FxState(double EurUsd = 1.08, double AlertThreshold = 1.05, bool Alert = false)
{
    public FxState() : this(1.08, 1.05, false) { }
}

public static class FxRatesMonitorUnit
{
    [Synapse(typeof(Tick), "fx.rates.monitor", Every = "every 5m")]
    public static async Task<Reaction<FxState>> Poll(Tick tick, FxState state, [Slot] IHttpRead rates, CancellationToken ct)
    {
        var feed = await rates.GetJson("https://api.exchangerate.host/latest?base=EUR", ct);
        var rate = 1.08;
        if (feed.TryGetProperty("rates", out var r) && r.TryGetProperty("USD", out var u) && u.TryGetDouble(out var d))
        {
            rate = d;
        }

        var alert = rate < state.AlertThreshold;
        return Reaction<FxState>.Of(state with { EurUsd = rate, Alert = alert }, new FxPolled(rate));
    }

    [Synapse("fx.reset", "fx.rates.monitor")]
    public static FxState Reset(SignalEnvelope env, FxState state) => state with { Alert = false };

    [Synapse("fx.rates.monitor")]
    public static View Show(FxState state) => new("Live Currency & Crypto Monitor", new Column(
    [
        new Heading("Live Currency & Crypto Monitor", Subtitle: $"Current: {state.EurUsd:F4}"),
        new Row(
        [
            new Badge($"EUR/USD {state.EurUsd:F4}", Tone: state.Alert ? "error" : "accent"),
            new Badge(state.Alert ? "Threshold Breached" : "Normal", Tone: state.Alert ? "error" : "ok"),
        ]) { Gap = 8 },
        new Button("reset-fx", "Clear Alert", new Command(".", "fx.reset"))
        {
            Style = Mix.Style()
                .Tone(state.Alert ? UiTone.Danger : UiTone.Secondary)
                .Padding(14, 8)
                .Radius(10)
                .Motion(UiMotion.Snappy)
                .OnPress(static s => s.Scale(0.98)),
        },
    ]) { Gap = 8 });
}
