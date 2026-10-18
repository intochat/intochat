using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Storage;

// A hosted deployment's storage over the local journal (IntoChat.OS.Authority.Hosted). Registered as an
// instance; AddBrainStorage applies it after the journal is wired, before the kernel registration.
public interface IHostedStorageExtension
{
    void Configure(IServiceCollection services, IConfiguration configuration);
}
