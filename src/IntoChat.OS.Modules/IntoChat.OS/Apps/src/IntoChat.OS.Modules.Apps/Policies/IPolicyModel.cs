namespace IntoChat.OS.Modules.Apps;

public interface IPolicyModel
{
    Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct);
}
