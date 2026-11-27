using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Errors;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices;

internal sealed record DeviceThisResponse(string MachineId);

internal sealed record DeviceInstallRequest(string? Model);

internal sealed record DeviceEnrollLinkRequest(string? Uri = null, string? Server = null, string? Code = null);

internal sealed record DeviceErrorResponse(string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DeviceThisResponse))]
[JsonSerializable(typeof(DeviceInstallRequest))]
[JsonSerializable(typeof(DeviceEnrollLinkRequest))]
[JsonSerializable(typeof(DeviceErrorResponse))]
[JsonSerializable(typeof(DeviceView))]
[JsonSerializable(typeof(ConnectedComputeNode))]
[JsonSerializable(typeof(ConnectedComputeNode[]))]
[JsonSerializable(typeof(Link.LinkStatus))]
[JsonSerializable(typeof(PendingDeviceEnrollmentInfo))]
internal sealed partial class DevicesHttpJson : JsonSerializerContext;

internal static class DevicesEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = BrainRouteGroups.Brain(endpoints, "/devices").WithMetadata(BrainRouteGroups.GuestClosed.Instance);
        group.MapGet("/this", GetThisAsync);
        group.MapGet("/compute/nodes", GetComputeNodesAsync);
        group.MapGet("/link/status", GetLinkStatusAsync);
        group.MapGet("/link/pending", GetLinkPendingAsync);
        group.MapPost("/link/pending", PostLinkPendingAsync);
        group.MapPost("/link/pending/confirm", PostLinkPendingConfirmAsync);
        group.MapPost("/link/pending/cancel", PostLinkPendingCancelAsync);
        group.MapPost("/link/enroll", PostLinkEnrollAsync);
        group.MapPost("/link/leave", PostLinkLeaveAsync);
        group.MapGet("/{machineId}", GetDeviceAsync);
        group.MapPost("/{machineId}/install", PostInstallAsync);
    }

    private static async Task GetLinkStatusAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var link = SiloNeurons.Get<Link.IDeviceLink>(http.RequestServices, Link.IDeviceLink.Key(BrainScope.CurrentId()));
        var status = await link.Status().ConfigureAwait(false);
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            status,
            DevicesHttpJson.Default.LinkStatus,
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task GetLinkPendingAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var store = http.RequestServices.GetService<IPendingDeviceEnrollmentStore>();
        var pending = store?.Current;
        if (pending is null)
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            pending,
            DevicesHttpJson.Default.PendingDeviceEnrollmentInfo,
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task PostLinkPendingAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (server, code, parseError) = await ReadAndValidateEnrollRequestAsync(http).ConfigureAwait(false);
        if (parseError is not null || server is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, parseError ?? "Invalid enrollment payload.").ConfigureAwait(false);
            return;
        }

        var store = http.RequestServices.GetRequiredService<IPendingDeviceEnrollmentStore>();
        var pending = store.Stage(server, code);
        http.Response.StatusCode = StatusCodes.Status202Accepted;
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            pending,
            DevicesHttpJson.Default.PendingDeviceEnrollmentInfo,
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task PostLinkPendingConfirmAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var store = http.RequestServices.GetService<IPendingDeviceEnrollmentStore>();
        if (store is null || !store.TryConsume(out var server, out var code) || server is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status404NotFound, "No pending device link request to confirm.").ConfigureAwait(false);
            return;
        }

        try
        {
            var link = SiloNeurons.Get<Link.IDeviceLink>(http.RequestServices, Link.IDeviceLink.Key(BrainScope.CurrentId()));
            var status = await link.Join(server, code).ConfigureAwait(false);
            http.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                http.Response.Body,
                status,
                DevicesHttpJson.Default.LinkStatus,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, error.Message).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, error.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status422UnprocessableEntity, error.Message).ConfigureAwait(false);
        }
    }

    private static Task PostLinkPendingCancelAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        http.RequestServices.GetService<IPendingDeviceEnrollmentStore>()?.Clear();
        http.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private static async Task<(Uri? Server, Code Code, string? Error)> ReadAndValidateEnrollRequestAsync(HttpContext http)
    {
        DeviceEnrollLinkRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                http.Request.Body,
                DevicesHttpJson.Default.DeviceEnrollLinkRequest,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return (null, default, "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return (null, default, "Enrollment payload is required.");
        }

        if (!string.IsNullOrWhiteSpace(request.Uri))
        {
            return Link.DeviceEnrollUri.TryParse(request.Uri, out var server, out var code, out var validationError)
                ? (server, code, null)
                : (null, default, validationError ?? "Invalid enrollment URI.");
        }

        if (!Link.DeviceEnrollUri.TryParseServer(request.Server, out var parsedServer, out var serverError))
        {
            return (null, default, serverError ?? "Invalid server URL.");
        }

        if (!Link.DeviceEnrollUri.ValidateCode(request.Code, out var parsedCode, out var codeError))
        {
            return (null, default, codeError ?? "Invalid pairing code.");
        }

        return (parsedServer, parsedCode, null);
    }

    private static async Task PostLinkEnrollAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        DeviceEnrollLinkRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                http.Request.Body,
                DevicesHttpJson.Default.DeviceEnrollLinkRequest,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "Request body must be valid JSON.").ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "Enrollment payload is required.").ConfigureAwait(false);
            return;
        }

        Uri? server;
        Code code;
        string? validationError;
        if (!string.IsNullOrWhiteSpace(request.Uri))
        {
            if (!Link.DeviceEnrollUri.TryParse(request.Uri, out server, out code, out validationError))
            {
                await WriteErrorAsync(http, StatusCodes.Status400BadRequest, validationError ?? "Invalid enrollment URI.").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            if (!Link.DeviceEnrollUri.TryParseServer(request.Server, out server, out validationError)
                || !Link.DeviceEnrollUri.ValidateCode(request.Code, out code, out validationError))
            {
                await WriteErrorAsync(http, StatusCodes.Status400BadRequest, validationError ?? "Invalid server or pairing code.").ConfigureAwait(false);
                return;
            }
        }

        try
        {
            var link = SiloNeurons.Get<Link.IDeviceLink>(http.RequestServices, Link.IDeviceLink.Key(BrainScope.CurrentId()));
            var status = await link.Join(server!, code).ConfigureAwait(false);
            http.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                http.Response.Body,
                status,
                DevicesHttpJson.Default.LinkStatus,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, error.Message).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, error.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status422UnprocessableEntity, error.Message).ConfigureAwait(false);
        }
    }

    private static async Task PostLinkLeaveAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            var link = SiloNeurons.Get<Link.IDeviceLink>(http.RequestServices, Link.IDeviceLink.Key(BrainScope.CurrentId()));
            await link.Leave().ConfigureAwait(false);
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (UnauthorizedAccessException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status403Forbidden, error.Message).ConfigureAwait(false);
        }
    }

    private static async Task GetComputeNodesAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var router = http.RequestServices.GetService<IConnectedComputeRouter>();
        var nodes = router is null
            ? []
            : await router.ListNodesAsync(new DigitalBrain.BrainId(BrainScope.CurrentId()), http.RequestAborted).ConfigureAwait(false);

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            nodes,
            DevicesHttpJson.Default.ConnectedComputeNodeArray,
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task GetThisAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var thisMachine = http.RequestServices.GetRequiredService<MachineId>();
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            new DeviceThisResponse(thisMachine.Value),
            DevicesHttpJson.Default.DeviceThisResponse,
            http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task GetDeviceAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var machineId = http.Request.RouteValues["machineId"]?.ToString();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "Machine id is required.").ConfigureAwait(false);
            return;
        }

        try
        {
            var device = SiloNeurons.Get<IDevice>(http.RequestServices, IDevice.Key(BrainScope.CurrentId(), machineId));
            var view = await device.Read().ConfigureAwait(false);
            http.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                http.Response.Body,
                view,
                DevicesHttpJson.Default.DeviceView,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status503ServiceUnavailable, error.Message).ConfigureAwait(false);
        }
    }

    private static async Task PostInstallAsync(HttpContext http)
    {
        if (!IsDirectPersonCaller(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var machineId = http.Request.RouteValues["machineId"]?.ToString();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "Machine id is required.").ConfigureAwait(false);
            return;
        }

        DeviceInstallRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                http.Request.Body,
                DevicesHttpJson.Default.DeviceInstallRequest,
                http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "Request body must be valid JSON.").ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Model))
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, "A model id is required.").ConfigureAwait(false);
            return;
        }

        try
        {
            var device = SiloNeurons.Get<IDevice>(http.RequestServices, IDevice.Key(BrainScope.CurrentId(), machineId));
            await device.Install(new ModelId(request.Model.Trim())).ConfigureAwait(false);
            http.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        catch (ArgumentException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, error.Message).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException error)
        {
            await WriteErrorAsync(http, StatusCodes.Status503ServiceUnavailable, error.Message).ConfigureAwait(false);
        }
    }

    private static bool IsDirectPersonCaller(HttpContext http)
    {
        var caller = HttpCaller.Current(http);
        if (caller is not { Actor: Actor.Person person, Conversation: null })
        {
            return false;
        }
        return !AppPrincipal.IsApp(person.Principal);
    }

    private static async Task WriteErrorAsync(HttpContext http, int statusCode, string message)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            new DeviceErrorResponse(message),
            DevicesHttpJson.Default.DeviceErrorResponse,
            http.RequestAborted).ConfigureAwait(false);
    }
}
