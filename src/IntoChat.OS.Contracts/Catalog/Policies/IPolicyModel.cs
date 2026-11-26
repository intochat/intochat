namespace IntoChat.OS.Modules.Apps;

// The model the policy writer asks. The catalog owns the question; the module that owns the Creator's model answers it.
public interface IPolicyModel
{
    Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct);
}
