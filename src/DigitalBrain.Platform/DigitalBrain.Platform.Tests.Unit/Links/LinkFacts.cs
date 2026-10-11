using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Sequencer;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// T20-1/T20-2: two machines hold one QUIC link, each end proving it is the machine whose registered key endorsed
// its ephemeral certificate, for this deployment.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class LinkFacts
{
    [Fact(Timeout = 60_000)]
    public async Task ALinkRoundTripsARequestOnLoopback()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        using var a = Key();
        using var b = Key();
        await Register(trust, a);
        await Register(trust, b);
        var deployment = await Deployment(trust);
        var admits = await Registered(trust, a, b);

        await using var listener = await QuicLinkListener.Start(Loopback, End(LinkCertificates.Create(a, deployment, TimeProvider.System), deployment, admits), _ => { }, ct);
        await using var link = await QuicMachineLink.Dial(listener.LocalEndPoint, null, MachineKeys.IdOf(a.PublicKey),
            End(LinkCertificates.Create(b, deployment, TimeProvider.System), deployment, admits), LinkPath.Direct, ct);
        var payload = Marker();
        var reply = await link.Send(Request(payload), ct);

        Assert.Equal(payload, reply.Payload);
        Assert.Equal(LinkPath.Direct, link.Path);
        Assert.Equal(MachineKeys.IdOf(a.PublicKey), link.RemoteMachineId);
    }

    [Fact(Timeout = 60_000)]
    public async Task AStrangersCertificateIsRefusedAtTheHandshake()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        using var a = Key();
        using var stranger = Key();
        await Register(trust, a);
        var deployment = await Deployment(trust);
        var admits = await Registered(trust, a, stranger);

        await using var listener = await QuicLinkListener.Start(Loopback, End(LinkCertificates.Create(a, deployment, TimeProvider.System), deployment, admits), _ => { }, ct);

        await Assert.ThrowsAnyAsync<AuthenticationException>(async () =>
        {
            await using var link = await QuicMachineLink.Dial(listener.LocalEndPoint, null, MachineKeys.IdOf(a.PublicKey),
                End(LinkCertificates.Create(stranger, deployment, TimeProvider.System), deployment, _ => true), LinkPath.Direct, ct);
            await link.Send(Request([1]), ct);
        });
    }

    [Fact]
    public void AnEndorsementForAnotherCertificateIsRefused()
    {
        var time = TimeProvider.System;
        using var a = Key();
        using var endorsed = LinkCertificates.Create(a, "d1", time);
        var extension = endorsed.Extensions[DigitalBrainNames.LinkEndorsementOid]!;
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=copy", otherKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(extension.Oid!, extension.RawData, critical: false));
        using var copied = request.CreateSelfSigned(time.GetUtcNow().AddMinutes(-5), time.GetUtcNow().AddDays(1));

        Assert.Equal(MachineKeys.IdOf(a.PublicKey), LinkCertificates.Validate(endorsed, "d1", time));
        Assert.Null(LinkCertificates.Validate(copied, "d1", time));
    }

    // Fix round 1: a link certificate lives a day; both ends renew it before NotAfter, and the listener hands out the
    // current one per handshake, so links still form after the lifetime has passed.
    [Fact(Timeout = 60_000)]
    public async Task ALinkStillFormsAfterTheCertificateLifetime()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        using var a = Key();
        using var b = Key();
        await Register(trust, a);
        await Register(trust, b);
        var deployment = await Deployment(trust);
        var admits = await Registered(trust, a, b);
        var time = new global::Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var certificateA = new LinkCertificate(a, deployment, time);
        using var certificateB = new LinkCertificate(b, deployment, time);
        await using var listener = await QuicLinkListener.Start(Loopback, End(certificateA, deployment, admits, time), _ => { }, ct);

        await using (var first = await QuicMachineLink.Dial(listener.LocalEndPoint, null, MachineKeys.IdOf(a.PublicKey), End(certificateB, deployment, admits, time), LinkPath.Direct, ct))
        { await first.Send(Request([1]), ct); }
        time.Advance(LinkCertificates.Lifetime * 2);
        await using var later = await QuicMachineLink.Dial(listener.LocalEndPoint, null, MachineKeys.IdOf(a.PublicKey), End(certificateB, deployment, admits, time), LinkPath.Direct, ct);

        Assert.Equal([2], (await later.Send(Request([2]), ct)).Payload);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEndorsementFromAnotherDeploymentIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        using var a = Key();
        using var b = Key();
        await Register(trust, a);
        await Register(trust, b);
        var deployment = await Deployment(trust);
        var admits = await Registered(trust, a, b);
        using var foreign = LinkCertificates.Create(b, "another-deployment", TimeProvider.System);

        Assert.Null(LinkCertificates.Validate(foreign, deployment, TimeProvider.System));
        await using var listener = await QuicLinkListener.Start(Loopback, End(LinkCertificates.Create(a, deployment, TimeProvider.System), deployment, admits), _ => { }, ct);
        await Assert.ThrowsAnyAsync<AuthenticationException>(async () =>
        {
            await using var link = await QuicMachineLink.Dial(listener.LocalEndPoint, null, MachineKeys.IdOf(a.PublicKey),
                End(foreign, "another-deployment", _ => true), LinkPath.Direct, ct);
            await link.Send(Request([1]), ct);
        });
    }
}
