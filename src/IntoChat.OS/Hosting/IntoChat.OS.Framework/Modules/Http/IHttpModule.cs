using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Framework.Modules;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
