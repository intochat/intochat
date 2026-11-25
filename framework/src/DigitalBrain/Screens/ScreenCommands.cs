using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.UI;

namespace DigitalBrain;

/// <summary>Commands a view carries: a typed signal sent back to the view's own neuron.</summary>
public static class ScreenCommands
{
    /// <summary>
    /// The command that brings <paramref name="signal"/> to the <c>[On]</c> reaction of the view's own neuron ("."). Null members are
    /// left out of its args, so the view stays small and never names a reserved member ("by", "value") it does not set.
    /// </summary>
    public static Command Sends<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        return new Command(".", SignalAliases.Of(typeof(TSignal)), WithoutNulls(PooledJson.Encode(signal, typeof(TSignal))));
    }

    private static JsonElement WithoutNulls(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.EnumerateObject().Any(member => member.Value.ValueKind == JsonValueKind.Null)) { return args; }
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in args.EnumerateObject().Where(member => member.Value.ValueKind != JsonValueKind.Null)) { member.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var stripped = JsonDocument.Parse(buffer.WrittenMemory);
        return stripped.RootElement.Clone();
    }
}
