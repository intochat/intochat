using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Rules;
using DigitalBrain.Time;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Notifications;

namespace Units.BtcAlert;

[Signal("btcalert.pricedropped")]
public sealed record PriceDropped(decimal Price, decimal Threshold) : Signal;

[State("btcalert")]
public sealed record BtcAlertState(
    decimal Threshold = 85_000m,
    decimal LastPrice = 0m,
    bool Armed = true,
    int AlertsSent = 0)
{
    public BtcAlertState() : this(85_000m, 0m, true, 0) { }
}

public sealed record PositiveThreshold(decimal Value) : Rule("btcalert.positive-threshold")
{
    public override bool Broken => Value is <= 0m or > 10_000_000m;

    public override string Message => "Threshold must be between $0.01 and $10,000,000.";
}

public static class BtcAlertUnit
{
    private const string SpotUrl = "https://api.coinbase.com/v2/prices/BTC-USD/spot";

    [Synapse(typeof(Tick), "btcalert", Every = "every 1m")]
    public static Reaction<BtcAlertState> Poll(
        Tick tick,
        BtcAlertState state,
        [Slot] Asks<IHttpRead> market) =>
        Reaction<BtcAlertState>.Of(state)
            .Ask(market.GetJson(SpotUrl, key: "spot"));

    [Synapse(typeof(Answered), "btcalert")]
    public static Reaction<BtcAlertState> Heard(
        Answered all,
        Answer answer,
        BtcAlertState state,
        [Slot] Asks<INotifications> notify)
    {
        if (answer.Key != "spot" || !answer.Ok)
        {
            return Reaction<BtcAlertState>.Of(state);
        }

        if (!TryReadSpotPrice(answer.Result, out var price))
        {
            return Reaction<BtcAlertState>.Of(state);
        }

        var next = state with { LastPrice = price };
        if (!next.Armed || price >= next.Threshold)
        {
            return Reaction<BtcAlertState>.Of(next);
        }

        var alertIndex = next.AlertsSent + 1;
        var notifyArgs = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["title"] = $"BTC dropped below ${next.Threshold:N0}",
            ["body"] = $"Spot is now ${price:N2}",
            ["priority"] = "high",
        });

        return Reaction<BtcAlertState>.Of(
                next with { Armed = false, AlertsSent = alertIndex },
                new PriceDropped(price, next.Threshold))
            .Ask(notify.Call("Notify", $"drop-{alertIndex}", notifyArgs));
    }

    [Synapse("btcalert.setthreshold", "btcalert")]
    public static Reaction<BtcAlertState> SetThreshold(SignalEnvelope envelope, BtcAlertState state)
    {
        var value = ReadDecimalPayload(envelope.Payload);
        if (Rules.Judge("SetThreshold", new PositiveThreshold(value)) is { } rejected)
        {
            return Reaction<BtcAlertState>.Of(state, rejected);
        }

        return Reaction<BtcAlertState>.Of(state with { Threshold = value, Armed = true });
    }

    [Synapse("btcalert.rearm", "btcalert")]
    public static BtcAlertState Rearm(SignalEnvelope envelope, BtcAlertState state) =>
        state with { Armed = true };

    [Synapse("btcalert")]
    public static View View(BtcAlertState state) =>
        new("BTC Alert", new Surface(
        [
            new Row(
            [
                new StatCardNode("spot", "BTC / USD", state.LastPrice == 0m ? "—" : $"${state.LastPrice:N2}"),
                new StatCardNode("limit", "Alert Below", $"${state.Threshold:N0}"),
                new Badge(state.Armed ? "Armed" : "Triggered", state.Armed ? "accent" : "warning"),
            ]),
            new Field(
                Key: "threshold",
                Name: "Alert Threshold (USD)",
                Kind: FieldKind.Number,
                Placeholder: state.Threshold.ToString(CultureInfo.InvariantCulture),
                OnSubmit: new Emit("btcalert", "btcalert.setthreshold")),
            new Button("rearm", "Re-arm Alert", new Command(".", "btcalert.rearm"), Enabled: !state.Armed),
        ], Title: "Bitcoin Spot Monitor", Subtitle: "Coinbase Spot · Every 1m"));

    private static bool TryReadSpotPrice(JsonElement root, out decimal price)
    {
        price = 0m;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("amount", out var amount))
        {
            return false;
        }

        if (amount.ValueKind == JsonValueKind.String
            && decimal.TryParse(amount.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out price))
        {
            return true;
        }

        if (amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out price))
        {
            return true;
        }

        return false;
    }

    private static decimal ReadDecimalPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("value", out var value))
        {
            return 0m;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }
}
