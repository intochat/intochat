using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Apps.Widgetbook;

public enum WidgetbookSourceMode
{
    BundledStatic = 0,
    DevHotReload = 1,
}

[GenerateSerializer, Alias("ui.widgetbook.state")]
public sealed record WidgetbookState(
    [property: Id(0)] int Version = 0,
    [property: Id(1)] string ActiveStoryPath = "",
    [property: Id(2)] string ThemeMode = "dark",
    [property: Id(3)] WidgetbookSourceMode SourceMode = WidgetbookSourceMode.BundledStatic,
    [property: Id(4)] string BaseUrl = "/widgetbook")
{
    public WidgetbookState() : this(0, "", "dark", WidgetbookSourceMode.BundledStatic, "/widgetbook") { }

    public string CurrentUrl
    {
        get
        {
            var basePath = string.IsNullOrWhiteSpace(BaseUrl) ? "/widgetbook" : BaseUrl.TrimEnd('/');
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(ActiveStoryPath))
            {
                query.Add($"path={Uri.EscapeDataString(ActiveStoryPath)}");
            }
            if (!string.IsNullOrWhiteSpace(ThemeMode))
            {
                query.Add($"theme={Uri.EscapeDataString(ThemeMode)}");
            }
            return query.Count == 0 ? $"{basePath}/index.html" : $"{basePath}/index.html?{string.Join('&', query)}";
        }
    }
}

[Signal("ui.widgetbook-changed")]
public sealed record WidgetbookChanged(
    string WidgetbookId,
    int Version,
    string ActiveStoryPath,
    string ThemeMode,
    WidgetbookSourceMode SourceMode) : Signal;

[NeuronAlias("ui.widgetbook", Formerly = ["IntoChat.OS.Modules.UI.Widgetbook.IWidgetbook"])]
[DefaultNeuronType("ui.widgetbook")]
public interface IWidgetbook
{
    Task<WidgetbookState> NavigateStory(string storyPath, string? themeMode = null);
    Task<WidgetbookState> SwitchSource(WidgetbookSourceMode mode, string? customBaseUrl = null);
    Task<WidgetbookState> SetThemeMode(string themeMode);

    [ReadOnlyCall]
    Task<WidgetbookState> Read();
}
