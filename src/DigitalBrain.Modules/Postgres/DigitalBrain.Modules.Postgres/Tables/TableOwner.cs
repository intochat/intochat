using DigitalBrain.Framework;
using System.Text.Json;

namespace DigitalBrain.Postgres.Tables;

// The identity every app table and table index is keyed by: one brain, one installed app.
// The key is the grain key; it is parsed back wherever a guard needs to know whose grain it is.
public readonly record struct TableOwner(string BrainId, string AppId)
{
    public string Key => JsonSerializer.Serialize(new[] { BrainId, AppId });

    public static TableOwner Parse(string key)
    {
        string?[]? parts;
        try { parts = JsonSerializer.Deserialize<string?[]>(key); }
        catch (JsonException) { parts = null; }
        if (parts is not [{ } brain, { } app] || string.IsNullOrWhiteSpace(brain) || string.IsNullOrWhiteSpace(app))
        { throw new UnauthorizedAccessException("This storage key does not name a brain and an app."); }
        return new(brain, app);
    }

    // The owner a stamped caller may act as. Only trusted callers reach table neurons, and a
    // caller is an owner only when the kernel stamped its app id.
    public static TableOwner Current(CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!CallerContextStamper.IsTrusted(caller) || string.IsNullOrWhiteSpace(caller.AppId))
        { throw new UnauthorizedAccessException("A trusted brain and app caller is required."); }
        return new(BrainScope.Create(caller.AccountId, caller.BrainId).Id, caller.AppId);
    }

    public static TableOwner Current() => Current(CallerContextStamper.Require());
}
