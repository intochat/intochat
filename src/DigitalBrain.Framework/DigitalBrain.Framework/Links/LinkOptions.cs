namespace DigitalBrain.Framework.Links;

public sealed class LinkOptions
{
    public const string Key = "DigitalBrain:Platform:Links";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(5);

    // How long both machines of a rendezvous send punch packets before QUIC takes their ports over (T20-6).
    public TimeSpan PunchFor { get; set; } = TimeSpan.FromSeconds(1);

    // A direct handshake not done this long after the rendezvous falls back to a relay slot.
    public TimeSpan DirectWithin { get; set; } = TimeSpan.FromSeconds(2);

    // How many links a listener holds from one IP address at once (a NAT may front several machines).
    public int MaxLinksPerAddress { get; set; } = 32;

    // The local UDP port the machine's trust link binds; 0 = allocated.
    public int Port { get; set; }

    public void Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero || KeepAlive <= TimeSpan.Zero || KeepAlive >= IdleTimeout)
        { throw new InvalidOperationException("Links need 0 < KeepAlive < IdleTimeout."); }
        if (PunchFor <= TimeSpan.Zero || DirectWithin <= TimeSpan.Zero)
        { throw new InvalidOperationException("Links need positive PunchFor and DirectWithin."); }
        if (Port is < 0 or > 65535) { throw new InvalidOperationException("Links Port must be 0..65535."); }
        if (MaxLinksPerAddress < 1) { throw new InvalidOperationException("Links MaxLinksPerAddress must be positive."); }
    }
}
