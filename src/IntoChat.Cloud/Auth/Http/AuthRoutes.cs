using DigitalBrain.Runtime.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// Auth's HTTP surface on the module seam (09-03): the host maps it once onto the session-required root group. Anonymous routes
// are marked AllowAnonymous one by one; every other route needs the signed-in session the group already demands, and
// AuthSessionFilter additionally refuses a session the sessions table no longer holds live.
internal sealed class AuthHttpSurface : IModuleHttpSurface
{
    public void Map(IEndpointRouteBuilder brainGroup)
    {
        ArgumentNullException.ThrowIfNull(brainGroup);
        var routes = brainGroup.MapGroup("");
        routes.AddEndpointFilter<AuthSessionFilter>();
        LicenseEndpoints.Map(routes);
        SignUpEndpoints.Map(routes);
        CodeEndpoints.Map(routes);
        SessionEndpoints.Map(routes);
        AccountEndpoints.Map(routes);
        LoginEndpoints.Map(routes);
        ResetEndpoints.Map(routes);
        EmailChangeEndpoints.Map(routes);
        TotpEndpoints.Map(routes);
        GoogleEndpoints.Map(routes);
    }
}
