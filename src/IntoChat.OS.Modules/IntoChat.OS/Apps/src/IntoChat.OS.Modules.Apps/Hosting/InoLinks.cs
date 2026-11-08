using System.Text.Json;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

// The pasted links Ino keeps in its own state, as the chat's link store reads them (ChatAppCard.FindLink); null when this host has
// not loaded the brain's Ino.
public static class InoLinks
{
    public static string? Json(IServiceProvider services, string brain)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<InProcessAppHost>()?.TryGetTurns(IApp.Key(brain, "ino"))?.App is DigitalBrain.App<InoAppState> ino
            ? JsonSerializer.Serialize(ino.State.Links, JsonSerializerOptions.Web) : null;
    }
}
