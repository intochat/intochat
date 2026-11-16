using DigitalBrain.Contracts;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.WebBrowser;

[NeuronAlias("webbrowser.connector"), DefaultNeuronType(UIVocabulary.WebBrowserType)]
internal interface IWebBrowserConnector
{
    Task Connect(int port, string sessionId);
    Task Disconnect(string sessionId);
    Task Unavailable(string reason);
}
