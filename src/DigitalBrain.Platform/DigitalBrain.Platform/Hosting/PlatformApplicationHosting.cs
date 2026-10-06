using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Directory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace DigitalBrain.Platform;

public static class PlatformApplicationHosting
{
    public static IHostApplicationBuilder AddDigitalBrainPlatform(this IHostApplicationBuilder builder)
    {
        builder.Services.AddCookieProtection();
        builder.UseOrleans(silo => silo.AddDigitalBrainPlatform());
        return builder;
    }
}
