using System.Text.Json;

namespace DigitalBrain.Access;

// The owner key an upload session is bound to: who the stamped caller is, so only the same caller continues the upload.
public static class UploadScope
{
    public static string Of(Caller? caller)
    {
        if (caller is null) { throw new UnauthorizedAccessException("An upload is started only by a stamped caller."); }
        return JsonSerializer.Serialize(new[]
        {
            caller.For?.Account, caller.Brain.Value, CallerRules.ActsFor(caller), caller.Actor.GetType().Name,
            CallerRules.IsApp(caller, out var app) ? app : null, caller.Conversation,
        });
    }
}
