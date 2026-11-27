namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Provides the normalized host folder paths consented by the local user (e.g. via <c>WindowsStorageSetup</c> / <c>IWindows.Roots()</c>).
/// Exposed in contracts so <c>DevicesModule</c> can confine Mode B folder operations without referencing <c>WindowsModule</c>.
/// </summary>
public interface IConsentedFolderRootProvider
{
    Task<string[]> GetConsentedRootsAsync(string brainId, CancellationToken cancellationToken = default);
}
