using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;

namespace IntoChat.OS.Flutter.WebBrowser;

[Alias("webbrowser.connector"), Orleans.Metadata.DefaultGrainType(UIVocabulary.WebBrowserType)]
public interface IWebBrowserConnector : IGrainWithStringKey
{
    Task Connect(int port, string sessionId);
    Task Disconnect(string sessionId);
    Task Unavailable(string reason);
}
