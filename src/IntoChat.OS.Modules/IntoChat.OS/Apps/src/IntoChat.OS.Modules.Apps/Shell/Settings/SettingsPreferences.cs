using System.Text.Json;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.TextField;

// Preference persistence: save, reload and reconcile, with the outcome (saved, reloaded or conflicted) in the status neuron.
// Preferences are the brain's, not the installation's: keyed by the brain scope they survive the app being renamed,
// reinstalled or forked.
public sealed class SettingsPreferences(IntoChatOSConnection brain)
{
    private readonly string _app = Settings.Key(brain);
    private string Root => brain.Setting("Brain") is { Length: > 0 } scope ? scope + "/settings" : _app;
    private ITextField Name => brain.Get<ITextField>(Root + "/display-name");
    private ITextField Theme => brain.Get<ITextField>(Root + "/theme");
    private ITextField DraftName => brain.Get<ITextField>(_app + "/preferences/display-name");
    private ITextField DraftTheme => brain.Get<ITextField>(_app + "/preferences/theme");
    private IText Baseline => brain.Get<IText>(_app + "/preferences/baseline");
    private IText Status => brain.Get<IText>(_app + "/preferences/status");

    // Whoever asked reads {app}/preferences.
    public async Task Publish()
    {
        var name = (await Name.Read()).Value;
        var chosen = (await Theme.Read()).Value;
        await brain.Get<IText>(_app + "/preferences").Set(JsonSerializer.Serialize(new
        {
            displayName = name.Length > 120 ? name[..120] : name,
            theme = chosen is "dark" or "light" ? chosen : "system",
        }));
    }

    // The Save button carries the draft and baseline; a malformed baseline is sent as none, which Save rejects with a status.
    public async Task<string> Submission()
        => JsonSerializer.Serialize(new Submission((await DraftName.Read()).Value, (await DraftTheme.Read()).Value,
            ParseBaseline((await Baseline.Read()).Markdown)));

    public async Task Run(string operation, string input)
    {
        try
        {
            if (operation == "save-preferences") { await Save(input); }
            else { await Reload(operation == "reload-preferences"); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Status.Set(error is PreferenceConflictException ? error.Message
                : "Preferences could not be saved or loaded. Your draft is preserved. Reload preferences to inspect the stored values before retrying.");
        }
    }

    private static PreferenceState? ParseBaseline(string json)
    {
        if (string.IsNullOrEmpty(json)) { return null; }
        try { return JsonSerializer.Deserialize<PreferenceState>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<PreferenceState> Current()
    {
        var n = await Name.Read(); var t = await Theme.Read();
        return new(n.Value, t.Value is "light" or "dark" ? t.Value : "system", n.Version, t.Version);
    }

    private async Task Reload(bool discard)
    {
        var current = await Current(); var previous = ParseBaseline((await Baseline.Read()).Markdown);
        var nameDraft = await DraftName.Read(); var themeDraft = await DraftTheme.Read();
        var clean = previous is null || (nameDraft.Value == previous.DisplayName && themeDraft.Value == previous.Theme);
        if (discard || clean)
        {
            if (!await DraftName.TrySetValue(current.DisplayName, nameDraft.Version) || !await DraftTheme.TrySetValue(current.Theme, themeDraft.Version))
            { throw new PreferenceConflictException("Your draft changed while preferences were loading. Review it or reload preferences."); }
            await Baseline.Set(JsonSerializer.Serialize(current));
            await Status.Set(discard ? "Stored preferences loaded." : "");
        }
        else if (previous != current) { await Status.Set("Preferences changed elsewhere. Reload preferences before saving your changes."); }
    }

    private async Task Save(string input)
    {
        await Status.Set("Saving preferences…");
        Submission? submitted;
        try { submitted = JsonSerializer.Deserialize<Submission>(input); }
        catch (JsonException) { submitted = null; }
        if (submitted?.DisplayName is null || submitted.Theme is null || submitted.Baseline is null)
        { throw new PreferenceConflictException("Reload preferences, then submit DisplayName, Theme and Baseline to save."); }
        var chosen = submitted.Theme.Trim().ToLowerInvariant();
        if (submitted.DisplayName.Length > 120 || chosen is not ("system" or "light" or "dark"))
        { throw new PreferenceConflictException("Use a name of at most 120 characters and a theme of system, light or dark."); }
        var saved = new PreferenceState(submitted.DisplayName, chosen,
            submitted.Baseline.NameVersion + (submitted.DisplayName != submitted.Baseline.DisplayName ? 1 : 0),
            submitted.Baseline.ThemeVersion + (chosen != submitted.Baseline.Theme ? 1 : 0));
        var stored = await Current();
        // Delivery is at-least-once: a redelivered save finds its own result stored and is not a conflict.
        if (stored == saved)
        {
            await Baseline.Set(JsonSerializer.Serialize(saved));
            await Status.Set("Preferences saved.");
            return;
        }
        if (stored != submitted.Baseline)
        { throw new PreferenceConflictException("Preferences changed elsewhere. Reload preferences before saving your changes."); }
        // Write only edited fields, so a name-only save never rewrites the stored theme.
        if (submitted.DisplayName != submitted.Baseline.DisplayName && !await Name.TrySetValue(submitted.DisplayName, submitted.Baseline.NameVersion))
        { throw new PreferenceConflictException("The stored name changed. Reload preferences before retrying."); }
        if (chosen != submitted.Baseline.Theme && !await Theme.TrySetValue(chosen, submitted.Baseline.ThemeVersion))
        { throw new PreferenceConflictException("The stored theme changed. Your name may have been saved. Reload preferences to inspect stored values."); }
        // Never adopt an external update as the baseline for a draft that still shows the old value.
        if (await Current() != saved)
        { throw new PreferenceConflictException("Preferences changed while saving. Some changes may have saved. Reload preferences to inspect stored values."); }
        await Baseline.Set(JsonSerializer.Serialize(saved));
        await Status.Set("Preferences saved.");
    }
}
