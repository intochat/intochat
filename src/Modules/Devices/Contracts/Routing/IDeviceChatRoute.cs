using DigitalBrain;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.Devices;

// Registered only where a device relay runs (the cloud).
public interface IDeviceChatRoute
{
    // Online devices only; Where = Where.Device, Id = DeviceModelRef.ToModelId().
    Task<ModelCard[]> Models(BrainId owner, CancellationToken ct);

    // Returns null when unknown, owned by another brain, revoked, offline, or not advertised.
    Task<IChatClient?> Chat(BrainId owner, DeviceModelRef model, CancellationToken ct);
}

public sealed class DeviceUnavailableException(string reason) : Exception(reason);
