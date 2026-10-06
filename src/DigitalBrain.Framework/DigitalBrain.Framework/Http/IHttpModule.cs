using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Framework;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
