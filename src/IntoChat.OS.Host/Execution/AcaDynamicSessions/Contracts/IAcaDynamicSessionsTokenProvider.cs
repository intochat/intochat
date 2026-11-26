namespace IntoChat.OS.Host.Execution;

/// <summary>
/// Acquires short-lived Entra ID / Managed Identity bearer tokens for the Azure Container Apps
/// Dynamic Sessions pool management plane (<c>https://dynamicsessions.io/.default</c>).
/// </summary>
public interface IAcaDynamicSessionsTokenProvider
{
    Task<string> AcquireTokenAsync(string scope, CancellationToken cancellationToken = default);
}
