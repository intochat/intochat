// os-only: the os module HTTP route seam hosted by the kernel.
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Framework.Modules;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
