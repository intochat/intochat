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
using Microsoft.Extensions.Options;

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
    public bool? AllowClientCodeSandbox { get; set; }
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
        var allowClientCodeSandbox = ResolveAllowClientCodeSandbox(State);
        var next = new DeviceLinkRecord
        {
            State = LinkState.Connected,
            Server = server.ToString(),
            DeviceName = deviceName,
            JoinedAt = now,
            LastContactedAt = now,
            AllowClientCodeSandbox = allowClientCodeSandbox,
        };
        await Store(next);
        services.GetService<IClientCodeSandboxConsent>()?.Set(allowClientCodeSandbox);
        await SyncLocalComputeRouterAsync(next, DeviceKeys.Thumbprint(spki), now).ConfigureAwait(true);
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
            if (credentials.IsRevoked)
            {
                var revoked = WithState(LinkState.Revoked, State.LastContactedAt);
                await Store(revoked);
                await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
                return ToStatus(revoked);
            }

            var reset = new DeviceLinkRecord
            {
                State = LinkState.NotJoined,
                Server = null,
                DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
                JoinedAt = null,
                LastContactedAt = State.LastContactedAt,
                AllowClientCodeSandbox = State.AllowClientCodeSandbox,
            };
            await Store(reset);
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(reset);
        }

        try
        {
            var remote = await cloud.StatusAsync();
            if (remote.Revoked)
            {
                await credentials.Revoke();
                var revoked = WithState(LinkState.Revoked, State.LastContactedAt);
                await Store(revoked);
                await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
                return ToStatus(revoked);
            }

            var connected = WithState(LinkState.Connected, Context.Time.GetUtcNow());
            await Store(connected);
            return ToStatus(connected);
        }
        catch (DeviceLinkRevokedException error)
        {
            logger?.LogInformation(error, "Device link was revoked by the cloud.");
            await credentials.Revoke();
            var revoked = WithState(LinkState.Revoked, State.LastContactedAt);
            await Store(revoked);
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(revoked);
        }
        catch (HttpRequestException error)
        {
            logger?.LogWarning(error, "Device link status check failed; marking offline.");
            var offline = WithState(LinkState.Offline, State.LastContactedAt);
            await Store(offline);
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(offline);
        }
        catch (TaskCanceledException error)
        {
            logger?.LogWarning(error, "Device link status check timed out; marking offline.");
            var offline = WithState(LinkState.Offline, State.LastContactedAt);
            await Store(offline);
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(offline);
        }
    }

    public async Task<LinkStatus> SetAllowClientCodeSandbox(bool allow)
    {
        await RequireOwnerOrSettingsAsync();

        var next = new DeviceLinkRecord
        {
            State = State.State,
            Server = State.Server,
            DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
            JoinedAt = State.JoinedAt,
            LastContactedAt = State.LastContactedAt,
            AllowClientCodeSandbox = allow,
        };
        await Store(next);
        services.GetService<IClientCodeSandboxConsent>()?.Set(allow);

        if (next.State != LinkState.Connected)
        {
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(next);
        }

        var secrets = await credentials.Read();
        if (secrets is null)
        {
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(next);
        }

        var now = Context.Time.GetUtcNow();
        var thumbprint = ComputeKeyThumbprint(secrets.PrivateKeyPkcs8);
        var advertisement = BuildComputeCapabilityAdvertisement(next, allow, thumbprint, now);

        try
        {
            await cloud.ReportComputeCapabilityAsync(advertisement);
            next = new DeviceLinkRecord
            {
                State = next.State,
                Server = next.Server,
                DeviceName = next.DeviceName,
                JoinedAt = next.JoinedAt,
                LastContactedAt = now,
                AllowClientCodeSandbox = allow,
            };
            await Store(next);
        }
        catch (DeviceLinkRevokedException error)
        {
            logger?.LogInformation(error, "Device link was revoked while updating ICodeSandbox capability.");
            await credentials.Revoke();
            var revoked = new DeviceLinkRecord
            {
                State = LinkState.Revoked,
                Server = next.Server,
                DeviceName = next.DeviceName,
                JoinedAt = next.JoinedAt,
                LastContactedAt = next.LastContactedAt,
                AllowClientCodeSandbox = allow,
            };
            await Store(revoked);
            await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
            return ToStatus(revoked);
        }
        catch (HttpRequestException error)
        {
            logger?.LogWarning(error, "Could not push ICodeSandbox capability update to cloud; local consent persisted and will reconcile on next heartbeat.");
        }
        catch (TaskCanceledException error)
        {
            logger?.LogWarning(error, "Timed out pushing ICodeSandbox capability update to cloud; local consent persisted and will reconcile on next heartbeat.");
        }

        await SyncLocalComputeRouterAsync(next, thumbprint, now).ConfigureAwait(true);
        return ToStatus(next);
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
        await MarkComputeOfflineBestEffortAsync().ConfigureAwait(true);
        await Store(new DeviceLinkRecord
        {
            State = LinkState.NotJoined,
            Server = null,
            DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
            JoinedAt = null,
            LastContactedAt = null,
            AllowClientCodeSandbox = State.AllowClientCodeSandbox,
        });
    }

    private ComputeCapabilityAdvertisement BuildComputeCapabilityAdvertisement(
        DeviceLinkRecord record,
        bool allowClientCodeSandbox,
        string? thumbprint,
        DateTimeOffset now)
    {
        var compute = ResolveComputeOptions();
        var sandboxReady = record.State == LinkState.Connected && compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox);
        return new ComputeCapabilityAdvertisement(
            MachineId: thisMachine.Value,
            DeviceName: string.IsNullOrEmpty(record.DeviceName) ? DefaultDeviceName() : record.DeviceName,
            ConsentMode: compute.ConsentMode,
            SandboxReady: sandboxReady,
            SandboxRuntime: "unit-host",
            OsDescription: RuntimeInformation.OSDescription,
            MaxConcurrency: compute.MaxConcurrency,
            HeartbeatAt: now,
            KeyThumbprint: thumbprint,
            AllowClientCodeSandbox: allowClientCodeSandbox,
            AdvertisedCapabilities: ClientComputeConsentPolicy.CapabilitiesFor(sandboxReady));
    }

    private async Task SyncLocalComputeRouterAsync(DeviceLinkRecord record, string? thumbprint, DateTimeOffset now)
    {
        if (services.GetService<IConnectedComputeRouter>() is not { } router)
        {
            return;
        }

        var allow = ResolveAllowClientCodeSandbox(record);
        var advertisement = BuildComputeCapabilityAdvertisement(record, allow, thumbprint, now);
        await router.RecordHeartbeatAsync(Id.Brain, advertisement).ConfigureAwait(true);
        if (!advertisement.SandboxReady)
        {
            await router.MarkOfflineAsync(Id.Brain, thisMachine.Value).ConfigureAwait(true);
        }
    }

    private async Task MarkComputeOfflineBestEffortAsync()
    {
        if (services.GetService<IConnectedComputeRouter>() is { } router)
        {
            await router.MarkOfflineAsync(Id.Brain, thisMachine.Value).ConfigureAwait(true);
        }
    }

    private ComputeCapabilityOptions ResolveComputeOptions()
        => services.GetService<IOptionsMonitor<DevicesOptions>>()?.CurrentValue.Compute ?? new ComputeCapabilityOptions();

    private bool ResolveAllowClientCodeSandbox(DeviceLinkRecord record)
    {
        if (record.AllowClientCodeSandbox is { } persisted)
        {
            return persisted;
        }

        var compute = ResolveComputeOptions();
        var store = services.GetService<IClientCodeSandboxConsent>();
        return store is not null ? store.Resolve(compute.AllowClientCodeSandbox) : compute.AllowClientCodeSandbox;
    }

    private DeviceLinkRecord WithState(LinkState next, DateTimeOffset? lastContactedAt) => new()
    {
        State = next,
        Server = State.Server,
        DeviceName = string.IsNullOrEmpty(State.DeviceName) ? DefaultDeviceName() : State.DeviceName,
        JoinedAt = State.JoinedAt,
        LastContactedAt = lastContactedAt,
        AllowClientCodeSandbox = State.AllowClientCodeSandbox,
    };

    private LinkStatus ToStatus(DeviceLinkRecord record)
    {
        var allow = ResolveAllowClientCodeSandbox(record);
        var compute = ResolveComputeOptions();
        var advertisesSandbox = record.State == LinkState.Connected && compute.IsClientCodeSandboxConsentedWith(allow);
        return new(
            record.State,
            string.IsNullOrWhiteSpace(record.Server) ? null : new Uri(record.Server, UriKind.Absolute),
            string.IsNullOrEmpty(record.DeviceName) ? DefaultDeviceName() : record.DeviceName,
            record.LastContactedAt,
            allow,
            ClientComputeConsentPolicy.CapabilitiesFor(advertisesSandbox));
    }

    private static string? ComputeKeyThumbprint(byte[]? pkcs8)
    {
        if (pkcs8 is not { Length: > 0 })
        {
            return null;
        }

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return DeviceKeys.Thumbprint(key.ExportSubjectPublicKeyInfo());
    }

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

    private async Task RequireOwnerOrSettingsAsync()
    {
        var caller = Context.Caller;
        if (caller is null || caller.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("Only the brain's owner (or Settings acting for the owner) may change Connected Client Compute consent.");
        }

        var account = caller.Actor switch
        {
            Actor.Person person => person.Account,
            Actor.App app when IsSettingsAppKey(app.Key) => app.For.Account,
            _ => null,
        };

        if (string.IsNullOrEmpty(account))
        {
            throw new UnauthorizedAccessException("Only the brain's owner (or Settings acting for the owner) may change Connected Client Compute consent.");
        }

        var owner = await OwnerAccountAsync();
        if (string.IsNullOrEmpty(owner) || !string.Equals(owner, account, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only the brain's owner (or Settings acting for the owner) may change Connected Client Compute consent.");
        }
    }

    private static bool IsSettingsAppKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim();
        return string.Equals(normalized, "app.settings", StringComparison.Ordinal)
            || string.Equals(normalized, "settings", StringComparison.Ordinal)
            || normalized.EndsWith("/app.settings", StringComparison.Ordinal)
            || normalized.EndsWith("/apps/settings", StringComparison.Ordinal);
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
