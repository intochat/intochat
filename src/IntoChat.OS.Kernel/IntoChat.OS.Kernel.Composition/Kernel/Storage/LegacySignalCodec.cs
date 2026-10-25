using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Client.Runtime.V1;
using Orleans.Serialization;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, deleted in 06-08): an os Signal in the runtime BrainLog. Its type name is the os alias (or the CLR name of
// a signal without one). Its payload is the signal's JSON through the edge encoding (RuntimeProtocol.Json), which a plain [On]
// subscriber decodes when that alias is also a DigitalBrain [Signal] alias, plus one extra member, "$orleans": the signal in the
// Orleans binary form the os BrainLog kept. os readers decode that member, so every os signal reads back exactly as written
// (polymorphic and non-JSON members included); a payload without it is a plain neuron's record and is not an os signal.
internal static class LegacySignalCodec
{
    public const string OrleansMember = "$orleans";

    public static DigitalBrain.Runtime.Orleans.JournalSignalWrite Encode(Signal signal, Serializer serializer)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(serializer);
        var type = signal.GetType();
        var payload = Json(signal, type) ?? [];
        payload[OrleansMember] = Convert.ToBase64String(serializer.SerializeToArray<Signal>(signal));
        return new(NameOf(type), JsonSerializer.SerializeToUtf8Bytes(payload, RuntimeProtocol.Json));
    }

    public static Signal? Decode(ReadOnlyMemory<byte> json, Serializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(OrleansMember, out var bytes)
            || bytes.ValueKind != JsonValueKind.String)
        { return null; }
        return serializer.Deserialize<Signal>(bytes.GetBytesFromBase64());
    }

    // The JSON a plain subscriber reads; a signal the edge encoding cannot write carries only the Orleans form.
    private static JsonObject? Json(Signal signal, Type type)
    {
        try { return JsonSerializer.SerializeToNode(signal, type, RuntimeProtocol.Json) as JsonObject; }
        catch (NotSupportedException) { return null; }
        catch (JsonException) { return null; }
    }

    // The os alias; a signal without one (test and fixture records) is journaled under its CLR name.
    private static string NameOf(Type type) => type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias ?? type.FullName!;
}
