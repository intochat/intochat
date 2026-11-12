using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.State;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.WebBrowser;

[NeuronAlias("webbrowser"), DefaultNeuronType(UIVocabulary.WebBrowserType)]
public interface IWebBrowser
{
    Task Navigate(string uri, string? title = null);
    Task Configure(string playwrightKey);
    [DigitalBrain.Contracts.ReadOnlyCall] Task<WebBrowserView> Read();
}

// The shell reads the connection by name.
[JsonConverter(typeof(JsonStringEnumConverter<BrowserConnection>))]
public enum BrowserConnection { Waiting, Attached, Unavailable, Failed }

[ExportableState]
public sealed class WebBrowserState
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
    public string? PlaywrightKey { get; set; }
}

public sealed class WebBrowserView
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
    public BrowserConnection Connection { get; set; }
    public string Error { get; set; } = "";
    public string ActualUrl { get; set; } = "";
}

[Signal("ui.browser-observation")]
public sealed record BrowserObservationChanged(string Name) : DigitalBrain.Signal;
