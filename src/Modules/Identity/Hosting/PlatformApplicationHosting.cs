using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using IntoChat.OS.Modules.Identity.Accounts;
namespace IntoChat.OS.Modules.Identity.Hosting;

public static class PlatformApplicationHosting
{
    public static IHostApplicationBuilder AddIntoChatOSPlatform(this IHostApplicationBuilder builder)
    {
        builder.Services.AddCookieProtection();
        builder.UseOrleans(silo => silo.AddIntoChatOSPlatform());
        return builder;
    }
}
