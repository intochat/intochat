using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Identity;

// A deployment's additions to the platform HTTP surface (IntoChat.OS.Authority.Hosted): middleware that runs
// once the caller is authenticated, and routes mapped with the platform's.
public interface IAuthorityHttpExtension
{
    void UseAfterAuthentication(WebApplication app);
    void Map(IEndpointRouteBuilder endpoints);
}
