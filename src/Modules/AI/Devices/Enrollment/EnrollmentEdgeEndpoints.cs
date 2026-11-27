using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices;

// The edge the cloud relay calls for device enrollment (EnrollmentEdge). No session: each request is admitted only by a verified,
// unspent service assertion for exactly this request, and then runs as the Devices module in the brain the code or route names.
internal static class EnrollmentEdgeEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(EnrollmentEdge.Redeem, static http => Run(http, RedeemAsync)).AllowAnonymous();
        var brain = BrainRouteGroups.Session(endpoints, "/devices/enrollment").AllowAnonymous();
        brain.MapGet("", static http => Run(http, ListAsync));
        brain.MapGet("/live", static http => Run(http, LiveAsync));
    }

    private static async Task Run(HttpContext http, Func<HttpContext, Task<IResult>> handler) => await (await handler(http)).ExecuteAsync(http);

    private static async Task<IResult> RedeemAsync(HttpContext http)
    {
        if (await AdmittedBodyAsync(http, EnrollmentEdge.RedeemOperation) is not { } body) { return Refused; }
        RedeemRequest? request;
        try { request = JsonSerializer.Deserialize(body.Span, EnrollmentEdgeJson.Default.RedeemRequest); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request is null || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.MachineId)
            || string.IsNullOrWhiteSpace(request.KeyThumbprint))
        { return Results.BadRequest(); }

        // The redeemer finds the brain from the code and enters the Devices module there; nothing here names a brain.
        var outcome = await http.RequestServices.GetRequiredService<IDeviceEnrollmentRedeemer>()
            .Redeem(request.Code, request.MachineId, request.KeyThumbprint, request.MachineName ?? "");
        return Results.Json(RedeemResponse.From(outcome), EnrollmentEdgeJson.Default.RedeemResponse);
    }

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        if (await AdmittedBodyAsync(http, EnrollmentEdge.ListOperation) is null) { return Refused; }
        var brain = new BrainId((string)http.Request.RouteValues["brainId"]!);
        var devices = await AsDevicesModule(http, brain, enrollment => enrollment.List());
        return Results.Json(devices, EnrollmentEdgeJson.Default.EnrolledDeviceArray);
    }

    private static async Task<IResult> LiveAsync(HttpContext http)
    {
        if (await AdmittedBodyAsync(http, EnrollmentEdge.LiveOperation) is null) { return Refused; }
        var machine = http.Request.Query["machine"].ToString();
        var thumbprint = http.Request.Query["thumbprint"].ToString();
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(thumbprint)) { return Results.BadRequest(); }
        var brain = new BrainId((string)http.Request.RouteValues["brainId"]!);
        var live = await AsDevicesModule(http, brain, enrollment => enrollment.IsLive(machine, thumbprint));
        return Results.Json(new LiveResponse(live), EnrollmentEdgeJson.Default.LiveResponse);
    }

    private static IResult Refused { get; } = Results.StatusCode(StatusCodes.Status401Unauthorized);

    private static async Task<T> AsDevicesModule<T>(HttpContext http, BrainId brain, Func<IDeviceEnrollment, Task<T>> call)
    {
        var edge = http.RequestServices.GetRequiredService<EdgeAuthority>();
        using (edge.Enter(edge.Module(DevicesModule.ManifestName, brain)))
        {
            return await call(SiloNeurons.Get<IDeviceEnrollment>(http.RequestServices, IDeviceEnrollment.Key(brain.Value)));
        }
    }

    // The request body (empty for a GET) when the request's assertion admits it for this operation, else null. The binding covers
    // the request target exactly as it arrived, so an assertion for one brain, machine or code never admits another.
    private static async Task<ReadOnlyMemory<byte>?> AdmittedBodyAsync(HttpContext http, string operation)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (http.Request.ContentLength > EnrollmentEdge.MaxBodyBytes) { return null; }
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > EnrollmentEdge.MaxBodyBytes) { return null; }
            buffer.Write(chunk, 0, read);
        }
        var target = http.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw
            : http.Request.Path.ToUriComponent() + http.Request.QueryString.ToUriComponent();
        if (http.RequestServices.GetService<IServiceAssertions>() is not { } assertions) { return null; }
        var issuer = await assertions.Verify(http.Request.Headers.Authorization.ToString(), operation, http.Request.Method, target, buffer.ToArray(), http.RequestAborted);
        if (issuer is null) { return null; }
        return new ReadOnlyMemory<byte>(buffer.ToArray());
    }
}
