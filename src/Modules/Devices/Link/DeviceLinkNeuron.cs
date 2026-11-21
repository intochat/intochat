using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

// Non-secret persisted state of the PC's cloud link; tokens and the P-256 key live only in DeviceCredentials.
[ExportableState]
internal sealed class DeviceLinkRecord
{
    public LinkState State { get; set; } = LinkState.NotJoined;
    public string? Server { get; set; }
    public string DeviceName { get; set; } = "";
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? LastContactedAt { get; set; }
}

// Plain neuron at "{brain}/device-link" on the PC.
[Neuron("devices.link")]
internal sealed class DeviceLinkNeuron(
    IServiceProvider services,
    MachineId thisMachine,
    DeviceCredentials credentials,
    CloudClient cloud,
    ILogger<DeviceLinkNeuron>? logger = null) : Neuron<DeviceLinkRecord>, IDeviceLink
{
    public async Task<LinkStatus> Join(Uri server, Code code)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri
            || (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && server.IsLoopback)))
        {
            throw new ArgumentException("Server URL must be an absolute https:// URI (or loopback http://).", nameof(server));
        }

        await RequireOwnerAsPersonAsync();

        var deviceName = DefaultDeviceName();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var pkcs8 = key.ExportPkcs8PrivateKey();

        var request = new EnrollRequest(
            code.Value,
            Base64Url.EncodeToString(spki),
            thisMachine.Value,
            deviceName,
            RuntimeInformation.OSDescription);

        await cloud.EnrollAsync(server, request, pkcs8);

        var now = Context.Time.GetUtcNow();
        var next = new DeviceLinkRecord
        {
            State = LinkState.Connected,
            Server = server.ToString(),
            DeviceName = deviceName,
            JoinedAt = now,
            LastContactedAt = now,
        };
        await Store(next);
        return ToStatus(next);
    }

    public async Task<LinkStatus> Status()
    {
        if (State.State is LinkState.NotJoined or LinkState.Revoked)
        {
            return ToStatus(State);
        }

        var secrets = await credentials.Read();
        if (secrets is null)
        {
            var reset = new DeviceLinkRecord
            {
                State = LinkState.NotJoined,
                Server = null,
                DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
                JoinedAt = null,
                LastContactedAt = State.LastContactedAt,
            };
            await Store(reset);
            return ToStatus(reset);
        }

        try
        {
            var remote = await cloud.StatusAsync();
            if (remote.Revoked)
            {
                await credentials.Delete();
                var revoked = WithState(LinkState.Revoked, State.LastContactedAt);
                await Store(revoked);
                return ToStatus(revoked);
            }

            var connected = WithState(LinkState.Connected, Context.Time.GetUtcNow());
            await Store(connected);
            return ToStatus(connected);
        }
        catch (DeviceLinkRevokedException error)
        {
            logger?.LogInformation(error, "Device link was revoked by the cloud.");
            await credentials.Delete();
            var revoked = WithState(LinkState.Revoked, State.LastContactedAt);
            await Store(revoked);
            return ToStatus(revoked);
        }
        catch (HttpRequestException error)
        {
            logger?.LogWarning(error, "Device link status check failed; marking offline.");
            var offline = WithState(LinkState.Offline, State.LastContactedAt);
            await Store(offline);
            return ToStatus(offline);
        }
        catch (TaskCanceledException error)
        {
            logger?.LogWarning(error, "Device link status check timed out; marking offline.");
            var offline = WithState(LinkState.Offline, State.LastContactedAt);
            await Store(offline);
            return ToStatus(offline);
        }
    }

    public async Task Leave()
    {
        await RequireOwnerAsPersonAsync();

        if (await credentials.Read() is not null)
        {
            try
            {
                await cloud.LeaveAsync();
            }
            catch (DeviceLinkRevokedException error)
            {
                logger?.LogInformation(error, "Device link was already revoked when leaving.");
            }
            catch (HttpRequestException error)
            {
                logger?.LogWarning(error, "Could not reach cloud while leaving; clearing local credentials.");
            }
            catch (TaskCanceledException error)
            {
                logger?.LogWarning(error, "Timed out notifying cloud while leaving; clearing local credentials.");
            }
        }

        await credentials.Delete();
        await Store(new DeviceLinkRecord
        {
            State = LinkState.NotJoined,
            Server = null,
            DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
            JoinedAt = null,
            LastContactedAt = null,
        });
    }

    private DeviceLinkRecord WithState(LinkState next, DateTimeOffset? lastContactedAt) => new()
    {
        State = next,
        Server = State.Server,
        DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
        JoinedAt = State.JoinedAt,
        LastContactedAt = lastContactedAt,
    };

    private LinkStatus ToStatus(DeviceLinkRecord record)
        => new(
            record.State,
            string.IsNullOrWhiteSpace(record.Server) ? null : new Uri(record.Server, UriKind.Absolute),
            string.IsNullOrEmpty(record.DeviceName) ? DefaultDeviceName() : record.DeviceName,
            record.LastContactedAt);

    private string DefaultDeviceName()
        => string.IsNullOrWhiteSpace(Environment.MachineName) ? thisMachine.Value : Environment.MachineName;

    private async Task RequireOwnerAsPersonAsync()
    {
        var caller = Context.Caller;
        if (caller is not { Actor: Actor.Person person }
            || string.IsNullOrEmpty(person.Account)
            || caller.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("Only the brain's owner, acting as a person, may manage the device link.");
        }

        var owner = await OwnerAccountAsync();
        if (string.IsNullOrEmpty(owner) || !string.Equals(owner, person.Account, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only the brain's owner, acting as a person, may manage the device link.");
        }
    }

    private async Task<string?> OwnerAccountAsync()
    {
        if (services.GetService<IIdentityClient>() is { } identity)
        {
            return (await identity.OwnerOfBrain(Id.Brain.Value).ConfigureAwait(true))?.AccountId;
        }
        if (services.GetService<ICapabilityBroker>() is { } broker)
        {
            var id = await broker.Identity().ConfigureAwait(true);
            return (await id.OwnerOfBrain(Id.Brain.Value).ConfigureAwait(true))?.AccountId;
        }
        return null;
    }
}
