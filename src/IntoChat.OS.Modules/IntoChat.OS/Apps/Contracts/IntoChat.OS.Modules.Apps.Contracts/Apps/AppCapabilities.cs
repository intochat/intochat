using DigitalBrain.Capabilities;

namespace IntoChat.OS.Modules.Apps;

// Whether a connection's declared capabilities satisfy an app's required capability id. A connection lists what its
// module declares: a versioned id ("storage@1") or, transitionally, a bare 0.6 kind ("storage") read as major 1.
public static class AppCapabilities
{
    public static bool Satisfies(IEnumerable<string>? provided, string required)
    {
        if (!CapabilityId.TryParse(required, out var need)) { return false; }
        foreach (var declared in provided ?? [])
        {
            var text = declared.Contains('@', StringComparison.Ordinal) ? declared : declared + "@1";
            if (CapabilityId.TryParse(text, out var offered) && offered.Satisfies(need)) { return true; }
        }
        return false;
    }
}
