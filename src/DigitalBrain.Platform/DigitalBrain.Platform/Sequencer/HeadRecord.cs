using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Platform.Sequencer;

// One part's head (Q10): who holds the lease until when, and a payload sealed with the part key that names the
// log head. Signed by the holder; the sequencer can verify and withhold it, never read or forge it.
[GenerateSerializer, Alias("platform.sequencer.head")]
internal sealed class HeadRecord
{
    [Id(0)] public string PartHash { get; set; } = "";
    [Id(1)] public long Version { get; set; }
    [Id(2)] public string Holder { get; set; } = "";
    [Id(3)] public byte[] HolderKey { get; set; } = [];
    [Id(4)] public DateTimeOffset LeaseExpiry { get; set; }
    [Id(5)] public byte[] Payload { get; set; } = [];
    [Id(6)] public byte[] Signature { get; set; } = [];

    public static string HashOf(string partId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partId)));

    public byte[] SignedBytes()
    {
        using var buffer = new MemoryStream();
        void Field(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        Span<byte> number = stackalloc byte[8];
        Field(Encoding.UTF8.GetBytes(PartHash));
        BinaryPrimitives.WriteInt64BigEndian(number, Version); Field(number);
        Field(Encoding.UTF8.GetBytes(Holder));
        Field(HolderKey);
        BinaryPrimitives.WriteInt64BigEndian(number, LeaseExpiry.ToUnixTimeMilliseconds()); Field(number);
        Field(Payload);
        return buffer.ToArray();
    }
}
