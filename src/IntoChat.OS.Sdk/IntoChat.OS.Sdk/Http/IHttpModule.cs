using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Framework;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
