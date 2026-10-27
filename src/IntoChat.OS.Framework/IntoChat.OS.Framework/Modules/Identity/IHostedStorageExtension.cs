using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Modules.Identity;

// A hosted deployment's storage over the local journal. Registered as an
// instance; AddBrainStorage applies it after the journal is wired, before the kernel registration.
public interface IHostedStorageExtension
{
    void Configure(IServiceCollection services, IConfiguration configuration);
}
