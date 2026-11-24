using System.Text.Json;
using IntoChat.OS.Modules.Apps.Social;

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

    // Posts live in the post store. The snapshot keeps its shape: Profile.Posts carries the store's posts, newest first.
    // Without a post store the snapshot carries what the profile itself still holds.
    public static async Task<PortableBrainSnapshot> ExportAsync(
        string brainId,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio,
        IPosts? posts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(studio);

        var settingsState = await settings.Read();
        var profileState = await profile.Read();
        if (posts is not null)
        {
            var stored = (await posts.Since(0)).Posts;
            profileState = profileState with { Posts = [.. stored.OrderByDescending(static post => post.Sequence).Select(post => ProfilePosts.ToProfile(post, brainId.Trim()))] };
        }
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

    // The snapshot's posts go to the post store under their ids and dates, oldest first, for the owner only: an archive
    // says nothing about who a post was shown to in the brain it came from.
    public static async Task ImportAsync(
        PortableBrainSnapshot snapshot,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio,
        IPosts? posts = null)
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
            var kept = (p.Posts ?? []).Where(static post => !string.IsNullOrWhiteSpace(post.Id) && !string.IsNullOrWhiteSpace(post.Content)).ToArray();
            if (kept.Length > 0 && posts is null)
            {
                throw new InvalidOperationException($"The archive holds {kept.Length} post(s) and no post store was given to import them into; nothing of them was stored.");
            }
            foreach (var post in kept.Reverse())
            {
                await posts!.Publish(ProfilePosts.ToDraft(post));
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
