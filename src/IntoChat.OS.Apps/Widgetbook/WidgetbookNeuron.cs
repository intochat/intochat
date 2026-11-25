using DigitalBrain.Neurons;

namespace IntoChat.OS.Apps.Widgetbook;

[Neuron("ui.widgetbook")]
internal sealed class WidgetbookNeuron : Neuron<WidgetbookState>, IWidgetbook
{
    public async Task<WidgetbookState> NavigateStory(string storyPath, string? themeMode = null)
    {
        var normalizedPath = (storyPath ?? string.Empty).Trim();
        var normalizedTheme = string.IsNullOrWhiteSpace(themeMode)
            ? State.ThemeMode
            : NormalizeTheme(themeMode);

        var next = State with
        {
            Version = State.Version + 1,
            ActiveStoryPath = normalizedPath,
            ThemeMode = normalizedTheme,
        };
        await Save(next, new WidgetbookChanged(Id.FullKey(), next.Version, next.ActiveStoryPath, next.ThemeMode, next.SourceMode));
        return next;
    }

    public async Task<WidgetbookState> SwitchSource(WidgetbookSourceMode mode, string? customBaseUrl = null)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(customBaseUrl)
            ? customBaseUrl.Trim()
            : mode == WidgetbookSourceMode.DevHotReload
                ? "http://localhost:8090"
                : "/widgetbook";

        var next = State with
        {
            Version = State.Version + 1,
            SourceMode = mode,
            BaseUrl = baseUrl,
        };
        await Save(next, new WidgetbookChanged(Id.FullKey(), next.Version, next.ActiveStoryPath, next.ThemeMode, next.SourceMode));
        return next;
    }

    public async Task<WidgetbookState> SetThemeMode(string themeMode)
    {
        var normalized = NormalizeTheme(themeMode);
        if (string.Equals(State.ThemeMode, normalized, StringComparison.Ordinal))
        {
            return State;
        }

        var next = State with
        {
            Version = State.Version + 1,
            ThemeMode = normalized,
        };
        await Save(next, new WidgetbookChanged(Id.FullKey(), next.Version, next.ActiveStoryPath, next.ThemeMode, next.SourceMode));
        return next;
    }

    public Task<WidgetbookState> Read() => Task.FromResult(State);

    private static string NormalizeTheme(string themeMode)
        => string.Equals(themeMode?.Trim(), "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
}
