using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var root = brain.Setting("Brain") is { Length: > 0 } scope ? scope + "/settings" : appKey;
var app = brain.Get<IApp>(appKey);
var name = brain.Get<ITextField>(root + "/display-name");
var theme = brain.Get<ITextField>(root + "/theme");
var draftName = brain.Get<ITextField>(appKey + "/preferences/display-name");
var draftTheme = brain.Get<ITextField>(appKey + "/preferences/theme");
var baseline = brain.Get<IText>(appKey + "/preferences/baseline");
var status = brain.Get<IText>(appKey + "/preferences/status");
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
var loops = new[] { Operations(), Clicks("save-preferences"), Clicks("reload-preferences") };
await Task.WhenAny(loops);
lifetime.Cancel();
await Task.WhenAll(loops);

async Task Operations()
{
    await foreach (var _ in brain.On<AppInvoked>(app, Reconcile, lifetime.Token)) { await Reconcile(); }
}

async Task Clicks(string operation)
{
    await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(appKey + "/" + operation), lifetime.Token))
    {
        var input = operation == "save-preferences"
            ? JsonSerializer.Serialize(new Submission((await draftName.Read()).Value, (await draftTheme.Read()).Value, await ReadBaseline())) : "";
        await app.Invoke(new(clicked.RequestId == Guid.Empty ? Guid.NewGuid() : clicked.RequestId, operation, input));
    }
}

async Task Reconcile()
{
    foreach (var request in (await app.Pending()).Where(r => r.Operation is "save-preferences" or "reload-preferences" or "reconcile-preferences"))
    {
        try
        {
            if (request.Operation == "save-preferences") { await Save(request.Input); }
            else { await Reload(request.Operation == "reload-preferences"); }
            await app.Respond(new(request.Id, "{}", null));
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            var message = error is PreferenceConflictException ? error.Message
                : "Preferences could not be saved or loaded. Your draft is preserved. Reload preferences to inspect the stored values before retrying.";
            await status.Set(message);
            await app.Respond(new(request.Id, null, message));
        }
    }
}

async Task<PreferenceState> Current()
{
    var n = await name.Read(); var t = await theme.Read();
    return new(n.Value, t.Value is "light" or "dark" ? t.Value : "system", n.Version, t.Version);
}

async Task<PreferenceState?> ReadBaseline()
{
    var value = (await baseline.Read()).Markdown;
    return string.IsNullOrEmpty(value) ? null : JsonSerializer.Deserialize<PreferenceState>(value);
}

async Task Reload(bool discard)
{
    var current = await Current(); var previous = await ReadBaseline();
    var nameDraft = await draftName.Read(); var themeDraft = await draftTheme.Read();
    var clean = previous is null || (nameDraft.Value == previous.DisplayName && themeDraft.Value == previous.Theme);
    if (discard || clean)
    {
        if (!await draftName.TrySetValue(current.DisplayName, nameDraft.Version) || !await draftTheme.TrySetValue(current.Theme, themeDraft.Version))
        { throw new PreferenceConflictException("Your draft changed while preferences were loading. Review it or reload preferences."); }
        await baseline.Set(JsonSerializer.Serialize(current));
        await status.Set(discard ? "Stored preferences loaded." : "");
    }
    else if (previous != current) { await status.Set("Preferences changed elsewhere. Reload preferences before saving your changes."); }
}

async Task Save(string input)
{
    await status.Set("Saving preferences…");
    Submission? submitted;
    try { submitted = JsonSerializer.Deserialize<Submission>(input); }
    catch (JsonException) { submitted = null; }
    if (submitted?.DisplayName is null || submitted.Theme is null || submitted.Baseline is null)
    { throw new PreferenceConflictException("Reload preferences, then submit DisplayName, Theme and Baseline to save."); }
    var chosen = submitted.Theme.Trim().ToLowerInvariant();
    if (submitted.DisplayName.Length > 120 || chosen is not ("system" or "light" or "dark"))
    { throw new PreferenceConflictException("Use a name of at most 120 characters and a theme of system, light or dark."); }
    if (await Current() != submitted.Baseline)
    { throw new PreferenceConflictException("Preferences changed elsewhere. Reload preferences before saving your changes."); }
    // Write only edited fields, so a name-only save never rewrites the stored theme.
    if (submitted.DisplayName != submitted.Baseline.DisplayName && !await name.TrySetValue(submitted.DisplayName, submitted.Baseline.NameVersion))
    { throw new PreferenceConflictException("The stored name changed. Reload preferences before retrying."); }
    if (chosen != submitted.Baseline.Theme && !await theme.TrySetValue(chosen, submitted.Baseline.ThemeVersion))
    { throw new PreferenceConflictException("The stored theme changed. Your name may have been saved. Reload preferences to inspect stored values."); }
    var saved = new PreferenceState(submitted.DisplayName, chosen,
        submitted.Baseline.NameVersion + (submitted.DisplayName != submitted.Baseline.DisplayName ? 1 : 0),
        submitted.Baseline.ThemeVersion + (chosen != submitted.Baseline.Theme ? 1 : 0));
    // Never adopt an external update as the baseline for a draft that still shows the old value.
    if (await Current() != saved)
    { throw new PreferenceConflictException("Preferences changed while saving. Some changes may have saved. Reload preferences to inspect stored values."); }
    await baseline.Set(JsonSerializer.Serialize(saved));
    await status.Set("Preferences saved.");
}

sealed record PreferenceState(string DisplayName, string Theme, int NameVersion, int ThemeVersion);
sealed record Submission(string DisplayName, string Theme, PreferenceState? Baseline);

sealed class PreferenceConflictException(string message) : Exception(message);
