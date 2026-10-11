namespace DigitalBrain.Platform.Trust;

// Where the trust cluster is (T20-7, R34 replaced): a machine reaches it over its link to Endpoint; the trust host
// runs its own Orleans cluster (silo port only, no gateway) beside its link endpoint and relay.
public sealed class TrustOptions
{
    public const string Key = "DigitalBrain:Platform:Trust";

    public string ClusterId { get; set; } = "digitalbrain-trust";

    public string ServiceId { get; set; } = "digitalbrain-trust";

    public int SiloPort { get; set; } = 11211;

    // host:port of the trust host's link endpoint (UDP, T20-7). A machine with an Endpoint reaches the trust cluster
    // over its link; without one it hosts the trust grains in its own silo (Stage A, tests).
    public string? Endpoint { get; set; }

    // The trust host's id (MachineKeys.IdOf its identity key) a machine pins (T20-3); unset, the first one seen is
    // kept in {MachineDirectory}/trust.pin. Production requires it on own and cloud machines.
    public string? Pin { get; set; }

    // The trust host's own identity key, which endorses its link certificate (T20-3).
    public string IdentityPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "trust", "identity.key");

    // The trust host's link endpoint and relay ports (UDP); 0 = allocated.
    public int LinkPort { get; set; }

    public int RelayPort { get; set; }

    // The address the trust silo advertises and listens on (R66); unset, Orleans picks the first adapter.
    public string? AdvertisedAddress { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClusterId) || string.IsNullOrWhiteSpace(ServiceId))
        { throw new InvalidOperationException("Trust needs a ClusterId and a ServiceId."); }
        if (SiloPort is < 1 or > 65535) { throw new InvalidOperationException("Trust needs a SiloPort between 1 and 65535."); }
        if (AdvertisedAddress is { } address && !System.Net.IPAddress.TryParse(address, out _))
        { throw new InvalidOperationException($"Trust AdvertisedAddress '{address}' is not an IP address."); }
        if (LinkPort is < 0 or > 65535 || RelayPort is < 0 or > 65535)
        { throw new InvalidOperationException("Trust LinkPort and RelayPort must be 0..65535."); }
        if (Endpoint is { } endpoint && !TryParse(endpoint, out _, out _))
        { throw new InvalidOperationException($"Trust Endpoint '{endpoint}' is not host:port."); }
    }

    public static bool TryParse(string endpoint, out string host, out int port)
    {
        var colon = endpoint.LastIndexOf(':');
        host = colon > 0 ? endpoint[..colon] : "";
        port = 0;
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }
}
