using System.Text.Json;

namespace DigitalBrain.Runtime.InMemory.Tests.Unit;

// The runtime's default codec is Orleans'; this project runs no Orleans code generation, so its state uses JSON.
public sealed class JsonStateCodec : IStateCodec
{
    public byte CodecId => 2;

    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public T Deserialize<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes)!;
}
