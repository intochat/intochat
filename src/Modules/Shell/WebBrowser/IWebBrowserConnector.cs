using DigitalBrain.Contracts;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Flutter.WebBrowser;

[NeuronAlias("webbrowser.connector"), DefaultNeuronType(UIVocabulary.WebBrowserType)]
internal interface IWebBrowserConnector
{
    Task Connect(int port, string sessionId);
    Task Disconnect(string sessionId);
    Task Unavailable(string reason);
}
