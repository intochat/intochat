using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans;
using IntoChat.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Result of resolving an <see cref="AppPackage"/>'s folder and connector slots against a brain.</summary>
public sealed record SlotResolutionResult(
    bool Ready,
    bool NeedsApproval,
    MissingConnectorProposal[] MissingConnectors,
    FolderSlotBinding[] SuggestedBindings);

/// <summary>
/// Resolves <see cref="AppPackage.Needs"/> slots on a target brain: proposes enabling the Windows
/// connector when absent and suggests explicit folder consent (e.g. <c>%USERPROFILE%\Downloads</c>) when present.
/// </summary>
public sealed class SlotResolver(INeuronProxies? proxies = null)
{
    public async Task<SlotResolutionResult> ResolveAsync(
        string brainId,
        AppPackage package,
        FolderSlotBinding[]? approvedBindings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(package);

        var folderSlots = package.Needs.Slots
            .Where(slot => slot.Contract.Contains(nameof(IFolder), StringComparison.Ordinal) || slot.Contract == typeof(IFolder).FullName)
            .ToArray();

        if (folderSlots.Length == 0)
        {
            return new SlotResolutionResult(Ready: true, NeedsApproval: false, [], []);
        }

        var windowsConnected = await IsWindowsConnectedAsync(brainId, cancellationToken).ConfigureAwait(false);
        var approvedMap = (approvedBindings ?? []).ToDictionary(b => b.Slot, StringComparer.OrdinalIgnoreCase);

        if (!windowsConnected)
        {
            var proposals = folderSlots
                .Select(slot => new MissingConnectorProposal(
                    "windows",
                    "Enable Windows Connector",
                    $"App '{package.Title}' needs mediated access to folder slot '{slot.Name}'. Connect 'This PC' so the OS can broker access without mounting your disk into Docker.",
                    slot.Name,
                    OperatingSystem.IsWindows()))
                .ToArray();
            return new SlotResolutionResult(Ready: false, NeedsApproval: true, proposals, []);
        }

        var suggestions = new List<FolderSlotBinding>();
        var needsApproval = false;

        foreach (var slot in folderSlots)
        {
            if (approvedMap.TryGetValue(slot.Name, out var existing))
            {
                suggestions.Add(existing);
                continue;
            }

            var resolvedPath = ResolveSuggestedHostPath(slot.DefaultPath);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                suggestions.Add(new FolderSlotBinding(slot.Name, resolvedPath, slot.Access, AutoSuggested: true));
                needsApproval = true;
            }
            else if (slot.Required)
            {
                needsApproval = true;
            }
        }

        return new SlotResolutionResult(
            Ready: !needsApproval,
            NeedsApproval: needsApproval,
            [],
            [.. suggestions]);
    }

    private async Task<bool> IsWindowsConnectedAsync(string brainId, CancellationToken cancellationToken)
    {
        if (proxies is null)
        {
            return OperatingSystem.IsWindows();
        }

        try
        {
            var connections = await proxies.Get<IConnectionRequests>(DigitalBrain.NeuronId.Parse(IConnectionRequests.Key(brainId)))
                .ListConnections()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return connections.Any(conn => string.Equals(conn.Kind, "windows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(conn.Status, "connected", StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            NullLogger.Instance.LogSwallowed(error, "Could not query connections for Windows slot resolution; falling back to host OS check.");
            return OperatingSystem.IsWindows();
        }
    }

    internal static string ResolveSuggestedHostPath(string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(defaultPath))
        {
            return string.Empty;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = defaultPath
            .Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("~", userProfile, StringComparison.Ordinal);

        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }
}
