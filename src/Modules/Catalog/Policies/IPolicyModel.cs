namespace IntoChat.OS.Modules.Apps;

internal interface IPolicyModel
{
    Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct);
}
