using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Kernel;

public interface IHttpModule
{
    void Configure(IEndpointRouteBuilder endpoints);
}
