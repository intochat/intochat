using DigitalBrain.Access;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Http.Surface;

// The caller the authenticated edge entered for this request (AccountSession), read from the ambient.
public static class HttpCaller
{
    public static Caller? Current(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.RequestServices.GetService<ICallerAmbient>()?.Current;
    }

    // The person the stamped caller acts for: the person, or the person an app or agent acts for.
    public static Actor.Person RequiredPerson(HttpContext http)
        => Current(http)?.For ?? throw new UnauthorizedAccessException("The request carries no caller acting for a person.");
}
