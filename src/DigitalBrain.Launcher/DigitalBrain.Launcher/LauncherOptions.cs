namespace DigitalBrain.Launcher;

public sealed class LauncherOptions
{
    public const string Section = "Launcher";
    public required string Repository { get; init; }
    public string RevisionsPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "revisions.json");
    public int ApiPort { get; init; } = 5079;
    public int UiPort { get; init; } = 5080;
    public int McpPort { get; init; } = 5081;
    public int FirstBlockPort { get; init; } = 5090;
    public int BlockSize { get; init; } = 10;
    public string AppHostProject { get; init; } = "src/DigitalBrain.AppHost/DigitalBrain.AppHost.csproj";
    public string AzuriteBlobEndpoint { get; init; } = "http://127.0.0.1:10000/devstoreaccount1";
    public string CurrentContainer { get; init; } = "digitalbrain-v2-state";
}
