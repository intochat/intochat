using Microsoft.AspNetCore.Builder;

namespace IntoChat.OS.Sdk;

// A module's slice of the kernel's HTTP pipeline (browser callbacks, webhooks). Registered as a
// singleton from Module.Configure; the kernel maps every surface in one place, before its gates.
public interface IHttpSurface
{
    void Map(IApplicationBuilder app);
}
