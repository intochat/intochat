using System.Buffers.Binary;
using System.Text;
using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Links;

// What a machine key says about one ephemeral link certificate (T20-2): "the certificate whose public key hashes to
// this is mine, for this deployment, until NotAfter". Carried in the certificate's LinkEndorsementOid extension.
internal sealed record LinkEndorsement(byte[] MachinePublicKey, string DeploymentId, DateTimeOffset NotAfter, byte[] Signature)
{
    public static byte[] SignedBytes(byte[] certificateSpkiHash, string deploymentId, DateTimeOffset notAfter)
    {
        using var buffer = new MemoryStream();
        Signing.Field(buffer, "digitalbrain-link|"u8);
        Signing.Field(buffer, certificateSpkiHash);
        Signing.Field(buffer, Encoding.UTF8.GetBytes(deploymentId));
        Signing.Number(buffer, notAfter.ToUnixTimeMilliseconds());
        return buffer.ToArray();
    }

    public byte[] Encode()
    {
        using var buffer = new MemoryStream();
        Signing.Field(buffer, MachinePublicKey);
        Signing.Field(buffer, Encoding.UTF8.GetBytes(DeploymentId));
        Signing.Number(buffer, NotAfter.ToUnixTimeMilliseconds());
        Signing.Field(buffer, Signature);
        return buffer.ToArray();
    }

    public static LinkEndorsement? Decode(ReadOnlySpan<byte> bytes)
    {
        if (!Next(ref bytes, out var key) || !Next(ref bytes, out var deployment) || !Next(ref bytes, out var notAfter)
            || notAfter.Length != 8 || !Next(ref bytes, out var signature) || !bytes.IsEmpty)
        { return null; }
        try
        {
            return new LinkEndorsement(key.ToArray(), Encoding.UTF8.GetString(deployment),
                DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(notAfter)), signature.ToArray());
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool Next(ref ReadOnlySpan<byte> bytes, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (bytes.Length < 4) { return false; }
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (length < 0 || length > bytes.Length - 4) { return false; }
        field = bytes.Slice(4, length);
        bytes = bytes[(4 + length)..];
        return true;
    }
}
