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
