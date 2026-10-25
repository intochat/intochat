using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Sdk;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
