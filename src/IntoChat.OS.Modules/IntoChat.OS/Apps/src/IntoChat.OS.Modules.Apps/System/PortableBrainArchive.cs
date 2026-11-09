using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// A portable JSON snapshot of a brain's user settings, profile, and deployed Synapse unit bundles (.cs + sidecars).
/// Secrets are never included in a portable archive.
/// </summary>
public sealed record PortableBrainSnapshot(
    string BrainId,
    DateTimeOffset ExportedAt,
    UserSettingsState Settings,
    ProfileState Profile,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Units,
    string FormatVersion = PortableBrainArchive.CurrentFormatVersion);

public static class PortableBrainArchive
{
    public const string CurrentFormatVersion = "1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<PortableBrainSnapshot> ExportAsync(
        string brainId,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(studio);

        var settingsState = await settings.Read();
        var profileState = await profile.Read();
        var units = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var deployed in await studio.List())
        {
            var alias = deployed.SourceKey;
            if (string.IsNullOrWhiteSpace(alias)) { continue; }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
            foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
            if (files.Count > 0)
            {
                units[alias] = files;
            }
        }

        return new PortableBrainSnapshot(
            BrainId: brainId.Trim(),
            ExportedAt: DateTimeOffset.UtcNow,
            Settings: settingsState,
            Profile: profileState,
            Units: units);
    }

    public static string ToJson(PortableBrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Json);
    }

    public static PortableBrainSnapshot FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PortableBrainSnapshot>(json, Json)
            ?? throw new InvalidOperationException("Portable brain snapshot JSON is empty or invalid.");
    }

    public static async Task ImportAsync(
        PortableBrainSnapshot snapshot,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(studio);

        if (snapshot.Settings is { } s)
        {
            await settings.SetProfile(s.DisplayName, s.Theme, s.DefaultModel);
            await settings.SetUiIdentity(s.StylePreset, s.BarLayout);
            await settings.SetPreferences(s.ThemeMode, s.DesktopTone, s.Language, s.TimeZone, s.DateFormat);
            foreach (var variable in s.Variables ?? [])
            {
                if (!string.IsNullOrWhiteSpace(variable.Key))
                {
                    await settings.SetVariable(variable.Key, variable.Value ?? "");
                }
            }
        }

        if (snapshot.Profile is { } p)
        {
            await profile.UpdateProfile(p.Handle, p.DisplayName, p.Bio, p.PinnedApps ?? [], p.AvatarUrl);
            foreach (var friend in p.Friends ?? [])
            {
                if (!string.IsNullOrWhiteSpace(friend.BrainId))
                {
                    await profile.AddFriend(friend.BrainId, friend.Handle, friend.DisplayName, friend.Scope);
                }
            }
            foreach (var post in p.Posts ?? [])
            {
                if (!string.IsNullOrWhiteSpace(post.Id) && !string.IsNullOrWhiteSpace(post.Content))
                {
                    await profile.PublishAppPost(post);
                }
            }
        }

        if (snapshot.Units is { Count: > 0 } units)
        {
            foreach (var (alias, files) in units)
            {
                if (!string.IsNullOrWhiteSpace(alias) && files is { Count: > 0 })
                {
                    await studio.DeployFiles(alias, files, new Dictionary<string, string>(StringComparer.Ordinal));
                }
            }
        }
    }
}
