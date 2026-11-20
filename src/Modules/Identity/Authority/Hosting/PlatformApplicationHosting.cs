using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Directory;
using IntoChat.OS.Connections.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace IntoChat.OS.Authority;

public static class PlatformApplicationHosting
{
    public static IHostApplicationBuilder AddIntoChatOSPlatform(this IHostApplicationBuilder builder)
    {
        builder.Services.AddCookieProtection();
        builder.UseOrleans(silo => silo.AddIntoChatOSPlatform());
        return builder;
    }
}
