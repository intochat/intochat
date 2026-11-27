using DigitalBrain.Contracts;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Devices.Link;

public enum LinkState
{
    NotJoined = 0,
    Connected = 1,
    Offline = 2,
    Revoked = 3,
}

public sealed record LinkStatus(
    LinkState State,
    Uri? Server,
    string DeviceName,
    DateTimeOffset? LastContactedAt,
    bool AllowClientCodeSandbox = false,
    string[]? AdvertisedCapabilities = null,
    string? CircleId = null,
    bool AllowWindowsFolderGateway = false,
    bool AllowConsole = false,
    string[]? ConsentedFolderRoots = null,
    bool AllowConnectedEngineering = false);

// One device-link neuron per brain on the PC at "{brain}/device-link".
[NeuronAlias("devices.link"), DefaultNeuronType("devices.link")]
public interface IDeviceLink
{
    Task<LinkStatus> Join(Uri server, Code code);

    Task<LinkStatus> Status();

    Task<LinkStatus> SetAllowClientCodeSandbox(bool allow);

    Task<LinkStatus> SetAllowWindowsFolderGateway(bool allow) => Status();

    Task<LinkStatus> SetAllowConsole(bool allow) => Status();

    Task<LinkStatus> SetAllowConnectedEngineering(bool allow) => Status();

    Task Leave();

    static string Key(string brain) => $"{brain}/device-link";
}
