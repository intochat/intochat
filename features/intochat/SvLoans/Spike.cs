using DigitalBrain.Client;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Flutter.Text;

static class Spike
{
    public static readonly string[] Operations = ["check-bureau", "score-risk", "send-otp", "create-payment", "record-payment"];

    // A slot reaches a provider only through the connection bound to it; an unbound slot has no neuron.
    public static string? Neuron(DigitalBrainConnection brain, string slot, string connector)
    {
        var id = brain.Setting("Account__" + slot) ?? "";
        return id.Length == 0 ? null : $"{brain.Setting("Brain")}/connection/{connector}/{id}";
    }

    public static string Unbound(string slot, string connector) =>
        $"Bind a {connector} connection to this app's {slot} slot in app configuration, then retry.";

    // The bound connection's neuron; an unbound slot throws, so Run records it as a failed result.
    public static string Require(DigitalBrainConnection brain, string slot, string connector) =>
        Neuron(brain, slot, connector) ?? throw new InvalidOperationException(Unbound(slot, connector));

    public static async Task Record(DigitalBrainConnection brain, string app, string operation, Guid requestId,
        string input, bool ok, string detail, string? providerStatus)
    {
        var json = new JsonObject
        {
            ["operation"] = operation,
            ["input"] = input,
            ["outcome"] = ok ? "ok" : "failed",
            ["detail"] = detail,
            ["providerStatus"] = providerStatus,
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
        }.ToJsonString();
        await brain.Get<IText>($"{app}/results/{operation}/{requestId:N}").Set(json);
        await brain.Get<IText>($"{app}/results/{operation}/latest").Set(json);
    }

    // One provider call, recorded: a provider failure becomes a failed result; cancellation propagates.
    public static async Task Run(DigitalBrainConnection brain, string app, string operation, Guid requestId, string input,
        Func<Task<(string Detail, string? Status)>> call)
    {
        try
        {
            var (detail, status) = await call();
            await Record(brain, app, operation, requestId, input, true, detail, status);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"{operation} {requestId:N} failed: {error.Message}");
            await Record(brain, app, operation, requestId, input, false, error.Message, null);
        }
    }

    public static JsonElement ParseObject(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidOperationException("Input must be a JSON object."); }
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new InvalidOperationException("Input must be a JSON object."); }
    }

    public static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
