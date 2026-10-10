using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Platform.Trust;

// A trust call that acts for an account (R37): the payload, the account it acts for, the machine that signs,
// and a timestamp and per-machine counter that make a replayed call stale. The trust side verifies the
// signature against the key the node directory recorded for that machine.
[GenerateSerializer, Alias("platform.trust.machine-signed")]
internal sealed record MachineSigned<T>(
    [property: Id(0)] T Payload,
    [property: Id(1)] string Account,
    [property: Id(2)] string MachineId,
    [property: Id(3)] DateTimeOffset Timestamp,
    [property: Id(4)] long Counter,
    [property: Id(5)] byte[] Signature)
{
    public SignedCall Call() => new(Account, MachineId, Timestamp, Counter, SignedBytes(Payload, Account, MachineId, Timestamp, Counter), Signature);

    // The payload is canonicalised as JSON of its public shape, so both sides hash the same bytes.
    public static byte[] SignedBytes(T payload, string account, string machineId, DateTimeOffset timestamp, long counter)
    {
        using var buffer = new MemoryStream();
        Signing.Field(buffer, Encoding.UTF8.GetBytes("machine-signed|" + typeof(T).Name));
        Signing.Field(buffer, SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
        Signing.Field(buffer, Encoding.UTF8.GetBytes(account));
        Signing.Field(buffer, Encoding.UTF8.GetBytes(machineId));
        Signing.Number(buffer, timestamp.ToUnixTimeMilliseconds());
        Signing.Number(buffer, counter);
        return buffer.ToArray();
    }
}

// What the machine registry checks for any signed call, whatever its payload.
[GenerateSerializer, Alias("platform.trust.signed-call")]
internal sealed record SignedCall(
    [property: Id(0)] string Account,
    [property: Id(1)] string MachineId,
    [property: Id(2)] DateTimeOffset Timestamp,
    [property: Id(3)] long Counter,
    [property: Id(4)] byte[] SignedBytes,
    [property: Id(5)] byte[] Signature);

internal static class Signing
{
    public static void Field(MemoryStream buffer, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }

    public static void Number(MemoryStream buffer, long value)
    {
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, value);
        Field(buffer, number);
    }

    // What a machine signs to register: its id, its key, the account and the pairing secret's hash (R52).
    public static byte[] Registration(string machineId, byte[] publicKey, string account, byte[] pairingSecretHash)
    {
        using var buffer = new MemoryStream();
        Field(buffer, Encoding.UTF8.GetBytes("machine-registration"));
        Field(buffer, Encoding.UTF8.GetBytes(machineId));
        Field(buffer, publicKey);
        Field(buffer, Encoding.UTF8.GetBytes(account));
        Field(buffer, pairingSecretHash);
        return buffer.ToArray();
    }
}
