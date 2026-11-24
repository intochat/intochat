using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit;

// The routes a mapping method adds, one line each: "METHOD /pattern", then " anonymous" when the route allows anonymous
// callers and " order=N" when it sets an order. Nothing is hosted: the endpoints are built and read, never called.
internal static class RouteTable
{
    public static string[] Of(Action<IEndpointRouteBuilder> map, Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddRouting();
        services?.Invoke(collection);
        using var provider = collection.BuildServiceProvider();
        var builder = new Builder(provider);
        map(builder);
        return [.. builder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => Methods(endpoint).Select(method => $"{method} {endpoint.RoutePattern.RawText}{Flags(endpoint)}"))
            .Order(StringComparer.Ordinal)];
    }

    // A service a handler takes as a parameter must be known to the container, or the route would read it from the body.
    public static IServiceCollection Known<T>(this IServiceCollection services) where T : class
        => services.AddSingleton<T>(_ => throw new InvalidOperationException($"{typeof(T).Name} is not available to a route table."));

    private static IEnumerable<string> Methods(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : ["ANY"];

    private static string Flags(RouteEndpoint endpoint)
        => (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null ? "" : " anonymous")
            + (endpoint.Order == 0 ? "" : $" order={endpoint.Order}");

    private sealed class Builder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
