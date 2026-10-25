using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Sdk.Identity;

// A deployment's additions to the platform HTTP surface: middleware that runs
// once the caller is authenticated, and routes mapped with the platform's.
public interface IAuthorityHttpExtension
{
    void UseAfterAuthentication(WebApplication app);
    void Map(IEndpointRouteBuilder endpoints);
}
