using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// Machine keys registered in a TrustHost and link ends made from them, for facts over raw links.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class LinkTestbed
{
    public static readonly Serializer Serializer = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    public static readonly LinkOptions Options = new() { IdleTimeout = TimeSpan.FromSeconds(5), KeepAlive = TimeSpan.FromSeconds(1) };

    public static void SkipWithoutQuic()
    {
        if (!QuicConnection.IsSupported || !QuicListener.IsSupported) { Assert.Skip("QUIC is not supported on this machine."); }
    }

    public static FileMachineSigningKey Key() => FileMachineSigningKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"link-{Guid.NewGuid():N}", "identity.key"));

    public static async Task Register(TrustHost trust, FileMachineSigningKey key, string account = ModuleTest.DefaultAccount)
    {
        var id = MachineKeys.IdOf(key.PublicKey);
        var pairing = MachineRegistrar.HashOf(trust.PairingSecret);
        await trust.Grains.GetGrain<IMachineRegistry>(id).Register(key.PublicKey, account, pairing,
            key.Sign(Signing.Registration(id, key.PublicKey, account, pairing)));
    }

    public static Task<string> Deployment(TrustHost trust) => trust.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).DeploymentId();

    // What a machine admits: exactly the registered machines among `known`, read from the trust side beforehand.
    public static async Task<Func<LinkPeer, bool>> Registered(TrustHost trust, params FileMachineSigningKey[] known)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in known)
        {
            var id = MachineKeys.IdOf(key.PublicKey);
            if (await trust.Grains.GetGrain<IMachineRegistry>(id).Read() is { } registered) { keys[id] = registered.PublicKey; }
        }
        return peer => keys.TryGetValue(peer.MachineId, out var registered) && registered.AsSpan().SequenceEqual(peer.PublicKey);
    }

    public static LinkEnd End(X509Certificate2 certificate, string deployment, Func<LinkPeer, bool> admits, LinkOptions? options = null)
        => new(certificate, deployment, admits, options ?? Options, Serializer, Echo, TimeProvider.System);

    public static Task<LinkReply> Echo(QuicMachineLink link, LinkRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new LinkReply(request.Payload, null, null));

    public static LinkRequest Request(byte[] payload) => new("echo", payload, null, null, Guid.NewGuid());

    public static readonly byte[] MarkerText = "digitalbrain-plaintext-marker"u8.ToArray();

    public static byte[] Marker(int size = 1024)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        MarkerText.CopyTo(bytes, 0);
        return bytes;
    }

    public static IPEndPoint Loopback => new(IPAddress.Loopback, 0);
}
