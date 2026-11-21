using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices;

[ExportableState]
internal sealed class DeviceEnrollmentRecord
{
    public DeviceCode[] Codes { get; set; } = [];
    public EnrolledDevice[] Devices { get; set; } = [];
}

internal sealed record DeviceCode(
    string Hash,
    DateTimeOffset ExpiresAt,
    string? MachineId,
    string? KeyThumbprint,
    string MachineName,
    DateTimeOffset? AcceptedAt);

// Plain neuron at "{brain}/device-enrollment": single-use device enrollment codes and the brain's enrolled machines.
[Neuron("devices.enrollment")]
internal sealed class DeviceEnrollmentNeuron(IServiceProvider services) : Neuron<DeviceEnrollmentRecord>, IDeviceEnrollment
{
    private ICodeBook Book => services.GetRequiredService<ICodeBook>();

    public async Task<Code> Mint()
    {
        await RequireOwnerAsPersonAsync();
        var now = Context.Time.GetUtcNow();
        var code = Code.New();
        var expiresAt = now + Code.Validity;
        var hash = await Book.Bind(code, CodePurpose.Device, Id.Brain, "", expiresAt);
        var kept = State.Codes.Where(existing => existing.AcceptedAt is not null || existing.ExpiresAt > now);
        await Store(new DeviceEnrollmentRecord
        {
            Codes = [.. kept, new DeviceCode(hash, expiresAt, null, null, "", null)],
            Devices = State.Devices,
        });
        return code;
    }

    public async Task<EnrolledDevice[]> List()
    {
        if (!IsCircleServerModuleCaller())
        {
            await RequireOwnerAsPersonAsync();
        }
        return State.Devices;
    }

    public async Task Revoke(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        await RequireOwnerAsPersonAsync();
        if (!State.Devices.Any(device => string.Equals(device.MachineId, machineId, StringComparison.Ordinal) && !device.Revoked))
        {
            return;
        }
        var updated = State.Devices
            .Select(device => string.Equals(device.MachineId, machineId, StringComparison.Ordinal)
                ? device with { Revoked = true }
                : device)
            .ToArray();
        await Store(new DeviceEnrollmentRecord
        {
            Codes = State.Codes,
            Devices = updated,
        });
    }

    public async Task<EnrollmentOutcome> Redeem(string codeHash, string machineId, string keyThumbprint, string machineName)
    {
        var entry = State.Codes.FirstOrDefault(known => Code.HashesEqual(known.Hash, codeHash));
        if (entry is null) { return EnrollmentOutcome.Rejected; }

        var now = Context.Time.GetUtcNow();
        var owner = await OwnerAccountAsync();
        if (string.IsNullOrEmpty(owner)) { return EnrollmentOutcome.Rejected; }

        if (entry.AcceptedAt is { } acceptedAt)
        {
            var revoked = State.Devices.Any(d => string.Equals(d.MachineId, entry.MachineId, StringComparison.Ordinal) && d.Revoked);
            if (revoked) { return EnrollmentOutcome.Rejected; }
            return string.Equals(entry.MachineId, machineId, StringComparison.Ordinal)
                   && string.Equals(entry.KeyThumbprint, keyThumbprint, StringComparison.Ordinal)
                   && now - acceptedAt <= IDeviceEnrollment.RepeatWindow
                ? new EnrollmentOutcome(EnrollmentResult.Repeat, Id.Brain.Value, owner, entry.MachineName)
                : EnrollmentOutcome.Rejected;
        }

        if (now >= entry.ExpiresAt) { return EnrollmentOutcome.Expired; }

        if (string.IsNullOrWhiteSpace(machineId)
            || string.IsNullOrWhiteSpace(keyThumbprint)
            || State.Devices.Any(d => string.Equals(d.MachineId, machineId, StringComparison.Ordinal) && !d.Revoked))
        {
            return EnrollmentOutcome.Rejected;
        }

        var normalizedName = machineName ?? "";
        var accepted = entry with
        {
            MachineId = machineId,
            KeyThumbprint = keyThumbprint,
            MachineName = normalizedName,
            AcceptedAt = now,
        };
        var enrolled = new EnrolledDevice(machineId, normalizedName, keyThumbprint, now, Revoked: false);
        var nextCodes = State.Codes.Select(known => ReferenceEquals(known, entry) ? accepted : known).ToArray();
        var nextDevices = State.Devices
            .Where(d => !string.Equals(d.MachineId, machineId, StringComparison.Ordinal))
            .Append(enrolled)
            .ToArray();
        await Store(new DeviceEnrollmentRecord
        {
            Codes = nextCodes,
            Devices = nextDevices,
        });
        return new EnrollmentOutcome(EnrollmentResult.Accepted, Id.Brain.Value, owner, normalizedName);
    }

    public Task<bool> IsLive(string machineId, string keyThumbprint)
    {
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(keyThumbprint))
        {
            return Task.FromResult(false);
        }

        var live = State.Devices.Any(d =>
            !d.Revoked
            && string.Equals(d.MachineId, machineId, StringComparison.Ordinal)
            && string.Equals(d.KeyThumbprint, keyThumbprint, StringComparison.Ordinal));
        return Task.FromResult(live);
    }

    private bool IsCircleServerModuleCaller()
        => Context.Caller is { Actor: Actor.Module { Id: "circle-server" }, Brain: var callerBrain }
            && callerBrain == Id.Brain;

    private async Task<string> RequireOwnerAsPersonAsync()
    {
        var caller = Context.Caller;
        if (caller is not { Actor: Actor.Person person }
            || string.IsNullOrEmpty(person.Account)
            || caller.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("Only the brain's owner, acting as a person, may manage its devices.");
        }

        var owner = await OwnerAccountAsync();
        if (string.IsNullOrEmpty(owner) || !string.Equals(owner, person.Account, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Only the brain's owner, acting as a person, may manage its devices.");
        }

        return owner;
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
