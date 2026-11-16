namespace IntoChat.OS.Modules.Apps;

// Whether some connection in a brain provides a capability. Install asks this before anything lands; the runtime
// capability broker replaces this behind the same seam.
public interface ICapabilitySatisfaction
{
    Task<bool> Satisfied(string brain, string capability, CancellationToken ct);
}
