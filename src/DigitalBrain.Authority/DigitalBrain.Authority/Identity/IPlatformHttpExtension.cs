using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Authority.Identity;

// A deployment's additions to the platform HTTP surface (DigitalBrain.Authority.Hosted): middleware that runs
// once the caller is authenticated, and routes mapped with the platform's.
public interface IPlatformHttpExtension
{
    void UseAfterAuthentication(WebApplication app);
    void Map(IEndpointRouteBuilder endpoints);
}
