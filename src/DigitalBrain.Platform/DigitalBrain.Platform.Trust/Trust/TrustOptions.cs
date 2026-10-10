namespace DigitalBrain.Platform.Trust;

// Where the trust cluster is (R34). A machine without a Gateway hosts the trust grains in its own silo (Stage A,
// tests); with one it reaches them through a private client. The trust host listens on the same ids and ports.
public sealed class TrustOptions
{
    public const string Key = "DigitalBrain:Platform:Trust";

    // host:port of a trust silo's gateway.
    public string? Gateway { get; set; }

    public string ClusterId { get; set; } = "digitalbrain-trust";

    public string ServiceId { get; set; } = "digitalbrain-trust";

    public int SiloPort { get; set; } = 11211;

    public int GatewayPort { get; set; } = 30100;

    // The address the trust silo advertises and listens on (R66); unset, Orleans picks the first adapter.
    public string? AdvertisedAddress { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClusterId) || string.IsNullOrWhiteSpace(ServiceId))
        { throw new InvalidOperationException("Trust needs a ClusterId and a ServiceId."); }
        if (SiloPort is < 1 or > 65535 || GatewayPort is < 1 or > 65535)
        { throw new InvalidOperationException("Trust needs SiloPort and GatewayPort between 1 and 65535."); }
        if (AdvertisedAddress is { } address && !System.Net.IPAddress.TryParse(address, out _))
        { throw new InvalidOperationException($"Trust AdvertisedAddress '{address}' is not an IP address."); }
        if (Gateway is { } gateway && !TryParse(gateway, out _, out _))
        { throw new InvalidOperationException($"Trust Gateway '{gateway}' is not host:port."); }
    }

    public static bool TryParse(string gateway, out string host, out int port)
    {
        var colon = gateway.LastIndexOf(':');
        host = colon > 0 ? gateway[..colon] : "";
        port = 0;
        return colon > 0 && int.TryParse(gateway[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }
}
