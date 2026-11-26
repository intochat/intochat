using DigitalBrain.Capabilities;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Runtime;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Flutter.Settings;

// The member-writable preferences of a brain: the notification switches and the app a new tab opens, kept in the brain's settings
// (IUserSettings at {brain}/settings). The HTTP preference endpoints read and write through here. A brain that stored them before
// they moved there (one text field per preference under {brain}/settings) has them adopted once, on the first read.
internal static class BrainPreferenceStore
{
    public const string NewTabDefault = "ino";

    public static readonly (string Key, bool Default)[] NotificationKeys =
    [
        ("agent-finished", true),
        ("needs-approval", true),
        ("connection-errors", true),
        ("product-updates", false),
    ];

    // The OS's own services (ShippedApps.Services, which Desktop.Hidden also is) never appear as apps a person can choose.
    public static readonly HashSet<string> Hidden = [.. ShippedApps.Services];

    public sealed record Choice(string Name, string Title);

    // Installed apps (App callers or calls through the app proxy) cannot change a person's preferences,
    // as brain entries refuse them.
    public static bool IsAppCaller(DigitalBrain.Access.Caller? caller) => DigitalBrain.Access.CallerRules.IsApp(caller, out _);

    public static bool IsNotificationKey(string key) => NotificationKeys.Any(known => known.Key == key);

    private static string NotificationField(string scope, string key) => scope + "/settings/notifications/" + key;

    private static string NewTabField(string scope) => scope + "/settings/workspace/new-tab-app";

    private static IUserSettings Settings(INeuronProxies brain, string scope) => brain.Get<IUserSettings>(IUserSettings.Key(new DigitalBrain.BrainId(scope)));

    // The settings, after adopting what an earlier version stored as text fields: each part once, when it was never set.
    private static async Task<UserSettingsState> Read(INeuronProxies brain, string scope, CancellationToken ct)
    {
        var settings = Settings(brain, scope);
        var state = await settings.Read().WaitAsync(ct);
        if (state.Notifications is null)
        {
            var adopted = new List<NotificationSetting>();
            foreach (var (key, _) in NotificationKeys)
            {
                var stored = await brain.Get<ILegacyPreferenceField>(NotificationField(scope, key)).Value().WaitAsync(ct);
                if (stored is "on" or "off") { adopted.Add(new(key, stored == "on")); }
            }
            state = await settings.SetNotifications([.. adopted]).WaitAsync(ct);
        }
        if (state.NewTabApp is null)
        {
            var stored = await brain.Get<ILegacyPreferenceField>(NewTabField(scope)).Value().WaitAsync(ct);
            state = await settings.SetNewTabApp(stored ?? "").WaitAsync(ct);
        }
        return state;
    }

    private static bool NotificationOf(UserSettingsState state, string key)
        => state.Notifications?.LastOrDefault(setting => setting.Key == key) is { } set ? set.On : NotificationKeys.First(known => known.Key == key).Default;

    public static async Task<Dictionary<string, bool>> ReadNotifications(INeuronProxies brain, string scope, CancellationToken ct)
    {
        var state = await Read(brain, scope, ct);
        return NotificationKeys.ToDictionary(known => known.Key, known => NotificationOf(state, known.Key));
    }

    public static async Task WriteNotifications(INeuronProxies brain, string scope, IReadOnlyDictionary<string, bool> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Keys.Any(key => !IsNotificationKey(key))) { throw new ArgumentException("Unknown notification preference.", nameof(values)); }
        // Adopt first, so a stored switch this write does not name is not lost.
        await Read(brain, scope, ct);
        await Settings(brain, scope).SetNotifications([.. values.Select(pair => new NotificationSetting(pair.Key, pair.Value))]).WaitAsync(ct);
    }

    // The apps a new tab may open: every titled, live app of the caller's brain except the hidden ones, Ino first.
    public static async Task<Choice[]> NewTabChoices(INeuronProxies brain, IBrainObserver kernel, string scope, CancellationToken ct)
    {
        var found = new List<Choice>();
        await foreach (var instance in kernel.Instances<IApp>(ct))
        {
            if (!IntoChat.OS.Host.Primitives.AppRef.TryParse(new DigitalBrain.BrainId(scope), instance.Key, out var appRef) || Hidden.Contains(appRef.Name)) { continue; }
            var name = appRef.Name;
            var app = await brain.Get<IApp>(instance.Key).Read().WaitAsync(ct);
            if (!string.IsNullOrWhiteSpace(app.Manifest.Title)) { found.Add(new(name, app.Manifest.Title)); }
        }
        return [.. found.OrderBy(choice => choice.Name == NewTabDefault ? 0 : 1).ThenBy(choice => choice.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public static async Task<string> ReadNewTab(INeuronProxies brain, string scope, Choice[] choices, CancellationToken ct)
    {
        var stored = (await Read(brain, scope, ct)).NewTabApp;
        return choices.Any(choice => choice.Name == stored) ? stored! : NewTabDefault;
    }

    // False when the name is not one of the choices; nothing is written then.
    public static async Task<bool> WriteNewTab(INeuronProxies brain, IBrainObserver kernel, string scope, string? name, CancellationToken ct)
    {
        var choices = await NewTabChoices(brain, kernel, scope, ct);
        if (name is null || !choices.Any(choice => choice.Name == name)) { return false; }
        await Read(brain, scope, ct);
        await Settings(brain, scope).SetNewTabApp(name).WaitAsync(ct);
        return true;
    }
}
