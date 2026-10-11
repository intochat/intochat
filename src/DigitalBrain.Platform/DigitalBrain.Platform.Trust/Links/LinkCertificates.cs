using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Links;

// One end's link certificate, renewed before it expires: at half its lifetime the next handshake gets a fresh one
// (fix round 1), so a long-running machine or trust host keeps forming links after a day.
internal sealed class LinkCertificate(IMachineSigningKey key, string deploymentId, TimeProvider time) : IDisposable
{
    private readonly Lock _gate = new();
    private X509Certificate2? _current;
    private DateTimeOffset _renewAt;

    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is null || time.GetUtcNow() >= _renewAt)
                {
                    // The replaced certificate may still back a live connection; it is left to the finalizer.
                    _current = LinkCertificates.Create(key, deploymentId, time);
                    _renewAt = time.GetUtcNow() + LinkCertificates.Lifetime / 2;
                }
                return _current;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _current?.Dispose(); }
    }
}

// The peer a handshake proved: the id derived from the key that endorsed its certificate.
internal sealed record LinkPeer(string MachineId, byte[] PublicKey);

// A link certificate is ephemeral and the machine key only endorses it (T20-2), so a non-exportable TPM key never
// meets TLS. Validation needs no CA: the endorsement verifies, it names this very certificate's key, it is live and
// for this deployment (R75). Which machines a link end then admits is the caller's (registration, dialed id, pin).
internal static class LinkCertificates
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    public static X509Certificate2 Create(IMachineSigningKey key, string deploymentId, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(deploymentId);
        ArgumentNullException.ThrowIfNull(time);
        using var certificateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = time.GetUtcNow();
        var notAfter = now + Lifetime;
        var spkiHash = SHA256.HashData(certificateKey.ExportSubjectPublicKeyInfo());
        var endorsement = new LinkEndorsement(key.PublicKey, deploymentId, notAfter,
            key.Sign(LinkEndorsement.SignedBytes(spkiHash, deploymentId, notAfter)));
        var request = new CertificateRequest($"CN={MachineKeys.IdOf(key.PublicKey)}", certificateKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        request.CertificateExtensions.Add(new X509Extension(DigitalBrainNames.LinkEndorsementOid, Wrap(endorsement.Encode()), critical: false));
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), notAfter);
        // TLS on Windows needs a key it can reopen: the ephemeral one is carried through a PKCS#12 round trip.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    // The machine id, or null when refused. A null deployment accepts any (the trust link, whose host is pinned).
    public static string? Validate(X509Certificate2 presented, string? deploymentId, TimeProvider time) => Read(presented, deploymentId, time)?.MachineId;

    public static LinkPeer? Read(X509Certificate? presented, string? deploymentId, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (presented is null) { return null; }
        using var owned = presented as X509Certificate2 is null ? X509CertificateLoader.LoadCertificate(presented.GetRawCertData()) : null;
        var certificate = owned ?? (X509Certificate2)presented;
        if (certificate.Extensions[DigitalBrainNames.LinkEndorsementOid] is not { } extension
            || Unwrap(extension.RawData) is not { } raw
            || LinkEndorsement.Decode(raw) is not { } endorsement)
        { return null; }
        var spkiHash = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        if (!MachineKeys.Verify(endorsement.MachinePublicKey, LinkEndorsement.SignedBytes(spkiHash, endorsement.DeploymentId, endorsement.NotAfter), endorsement.Signature))
        { return null; }
        if (endorsement.NotAfter <= time.GetUtcNow()) { return null; }
        if (deploymentId is not null && !string.Equals(endorsement.DeploymentId, deploymentId, StringComparison.Ordinal)) { return null; }
        return new LinkPeer(MachineKeys.IdOf(endorsement.MachinePublicKey), endorsement.MachinePublicKey);
    }

    private static byte[] Wrap(byte[] value)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteOctetString(value);
        return writer.Encode();
    }

    private static byte[]? Unwrap(byte[] value)
    {
        try
        {
            var reader = new AsnReader(value, AsnEncodingRules.DER);
            var octets = reader.ReadOctetString();
            return reader.HasData ? null : octets;
        }
        catch (AsnContentException) { return null; }
    }
}
