using DigitalBrain.Runtime.Orleans.Capabilities;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

internal sealed class StorageWriteIdentity : IStorageWriteIdentity
{
    public string? Current(BrainId brain)
    {
        if (!RuntimeCallerContext.TryGet(out var caller) || !RuntimeCallerContext.IsTrusted(caller) || caller.BrainId != brain.Value)
        { return null; }
        return JsonSerializer.Serialize(new
        {
            caller.AccountId,
            caller.BrainId,
            caller.PrincipalId,
            caller.Kind,
            caller.StampedBy,
            caller.AppId,
            caller.OnBehalfOf,
            caller.ConversationId,
        });
    }
}
