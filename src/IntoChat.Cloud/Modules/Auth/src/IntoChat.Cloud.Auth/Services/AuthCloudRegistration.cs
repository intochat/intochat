using IntoChat.Cloud.Auth.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.Cloud.Auth.Services;

// Cloud seams the kernel registers before the module's TryAdd. The implementations stay internal to this assembly.
public static class AuthCloudRegistration
{
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAccountAuth, AccountAuth>();
        services.TryAddSingleton<CloudSignIn>();
        services.TryAddSingleton<ICloudSignIn>(provider => provider.GetRequiredService<CloudSignIn>());
    }
}
