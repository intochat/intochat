using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Flutter;

// UI-kit neurons hold per-brain window data: a route's {name} addresses the part at "{brain}/ui/{name}".
internal static class UiScope
{
    public static string Key(string scope, string name) => $"{scope}/ui/{name}";
}

internal static class UiHttp
{
    // Nothing in FlutterModule calls UiHttp anymore; kept internal for source compatibility.
}
