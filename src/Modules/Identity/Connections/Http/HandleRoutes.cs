using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity.Handles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Connections.Http;

public static class HandleRoutes
{
    public static string? Rewrite(string handle, string collection, string key, string brainId)
    {
        _ = handle;
        return collection switch
        {
            "apps" => $"/brains/{brainId}/apps/{key}",
            "files" => $"/brains/{brainId}/storage/{key}",
            _ => null,
        };
    }

    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet("/{handle}/{collection}/{key}", async (string handle, string collection, string key, HttpContext http) =>
        {
            if (!IHandleDirectory.IsValid(handle))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var normalizedHandle = IHandleDirectory.Normalize(handle);
            var handleDirectory = ResolveDirectory(http.RequestServices, normalizedHandle);
            var edge = http.RequestServices.GetService<EdgeAuthority>();

            string? brainId;
            using (edge?.Enter(edge.Module("identity.handles", new BrainId(IHandleDirectory.BrainOf(normalizedHandle)))))
            {
                brainId = await handleDirectory.Resolve();
            }

            if (string.IsNullOrWhiteSpace(brainId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var rewritten = Rewrite(normalizedHandle, collection, key, brainId);
            if (rewritten is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Request.Path = rewritten;
            http.Request.RouteValues["brainId"] = brainId;
            if (collection == "apps")
            {
                http.Request.RouteValues["appKey"] = key;
                http.Request.RouteValues["name"] = key;
            }

            // Find the endpoint matching the rewritten path and execute its RequestDelegate
            var targetEndpoint = endpoints.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .FirstOrDefault(ep =>
                {
                    var pattern = ep.RoutePattern.RawText ?? "";
                    var method = ep.Metadata.GetMetadata<HttpMethodMetadata>();
                    if (method is not null && !method.HttpMethods.Contains("GET"))
                    {
                        return false;
                    }

                    if (collection == "apps" && pattern.StartsWith("/brains/{brainId}/apps/", StringComparison.OrdinalIgnoreCase)
                        && ep.RoutePattern.PathSegments.Count == 4)
                    {
                        return true;
                    }

                    if (collection == "files" && pattern.StartsWith("/brains/{brainId}/storage/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return false;
                })
                ?? endpoints.DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .FirstOrDefault(ep =>
                {
                    var pattern = ep.RoutePattern.RawText ?? "";
                    if (collection == "apps" && pattern.StartsWith("/brains/{brainId}/apps/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (collection == "files" && pattern.StartsWith("/brains/{brainId}/storage/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return false;
                });

            if (targetEndpoint?.RequestDelegate is not null)
            {
                http.SetEndpoint(targetEndpoint);
                await targetEndpoint.RequestDelegate(http);
                return Results.Empty;
            }

            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });
    }

    private static IHandleDirectory ResolveDirectory(IServiceProvider services, string normalizedHandle)
    {
        if (services.GetService(typeof(Func<string, IHandleDirectory>)) is Func<string, IHandleDirectory> factory)
        {
            return factory(normalizedHandle);
        }

        if (services.GetService(typeof(IHandleDirectory)) is IHandleDirectory directory)
        {
            return directory;
        }

        return SiloNeurons.Get<IHandleDirectory>(services, IHandleDirectory.Key(normalizedHandle));
    }
}
