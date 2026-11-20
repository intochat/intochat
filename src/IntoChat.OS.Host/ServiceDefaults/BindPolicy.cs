using System.Net;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Host.ServiceDefaults;

public enum BindKind
{
    // local or local+cloud without IntoChatOS:Bind: loopback only.
    Loopback,
    // local or local+cloud with IntoChatOS:Bind: the listed addresses (and loopback).
    Explicit,
    // cloud: today's hosted rules (explicit posture, container exemption).
    Hosted,
}

// Where a process may listen, derived from IntoChatOS:Topology (unset = local) and IntoChatOS:Bind (D-21). A missing topology
// fails toward loopback; an unknown topology or a malformed bind stops startup naming the key, never the value, and never
// falls back to every interface.
public sealed record BindPolicy(BindKind Kind, IReadOnlyList<IPAddress> Addresses)
{
    public const string TopologyKey = IntoChatOSNames.TopologyKey;
    public const string BindKey = IntoChatOSNames.BindKey;

    public static BindPolicy From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var topology = configuration[TopologyKey];
        if (string.IsNullOrWhiteSpace(topology)
            || string.Equals(topology, IntoChatOSNames.TopologyLocal, StringComparison.OrdinalIgnoreCase)
            || string.Equals(topology, IntoChatOSNames.TopologyLocalCloud, StringComparison.OrdinalIgnoreCase)
            || string.Equals(topology, IntoChatOSNames.TopologyLocalCloudAlias, StringComparison.OrdinalIgnoreCase))
        {
            var addresses = ParseBind(configuration[BindKey]);
            return addresses.Count == 0 ? new BindPolicy(BindKind.Loopback, []) : new BindPolicy(BindKind.Explicit, addresses);
        }
        if (string.Equals(topology, IntoChatOSNames.TopologyCloud, StringComparison.OrdinalIgnoreCase)) { return new BindPolicy(BindKind.Hosted, []); }
        throw new InvalidOperationException(
            $"{TopologyKey} must be one of {IntoChatOSNames.TopologyLocal}, {IntoChatOSNames.TopologyLocalCloud} or {IntoChatOSNames.TopologyCloud} (unset means {IntoChatOSNames.TopologyLocal}).");
    }

    // True when the loopback rules apply: local or local+cloud on the host. A container keeps today's exemption.
    public static bool IsLocal(IConfiguration configuration)
        => From(configuration).Kind != BindKind.Hosted
            && !string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);

    public bool AllowsEveryInterface => Addresses.Any(address => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    public bool HasNonLoopback => Addresses.Any(address => !IPAddress.IsLoopback(address));

    // Whether a listener host (from a URL) is allowed. Loopback always is; under Explicit an IP host must be listed, and a
    // wildcard or name host (Kestrel binds every interface for those) needs 0.0.0.0 or :: in the bind.
    public bool Allows(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { return true; }
        var parsed = IPAddress.TryParse(host, out var address);
        if (parsed && IPAddress.IsLoopback(address!)) { return true; }
        if (Kind == BindKind.Hosted) { return true; }
        if (Kind == BindKind.Loopback) { return false; }
        if (AllowsEveryInterface) { return true; }
        return parsed && Addresses.Contains(address!);
    }

    private static List<IPAddress> ParseBind(string? value)
    {
        var addresses = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(value)) { return addresses; }
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { addresses.Add(IPAddress.Loopback); continue; }
            var literal = part.StartsWith('[') && part.EndsWith(']') ? part[1..^1] : part;
            // IPAddress.TryParse also accepts shorthand such as "1"; only dotted or colon literals are addresses here.
            if (!(literal.Contains('.') || literal.Contains(':')) || !IPAddress.TryParse(literal, out var address))
            {
                throw new InvalidOperationException(
                    $"{BindKey} must be a comma list of IP literals or localhost (0.0.0.0 or :: for every interface); remove it to listen on loopback only.");
            }
            addresses.Add(address);
        }
        return addresses;
    }
}
