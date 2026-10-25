using Microsoft.AspNetCore.Http;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Connections;

// The map from a module route's exceptions to HTTP; a group that throws plain neuron errors adds it once.
public static class ModuleRouteGuard
{
    public static async ValueTask<object?> Guard(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ServiceUnavailableException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status503ServiceUnavailable); }
        catch (FileNotFoundException) { return Results.Problem("The file no longer exists. Refresh Files.", statusCode: StatusCodes.Status404NotFound); }
        catch (KeyNotFoundException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (UnauthorizedAccessException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (RevisionConflictException error) { return Results.Json(new { error = error.Message, currentRevision = error.CurrentRevision }, statusCode: StatusCodes.Status409Conflict); }
        catch (InvalidOperationException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (InvalidDataException error) { return Results.Problem(error.Message, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (IOException) { return Results.Problem("The local file could not be accessed. Check its permissions and retry.", statusCode: StatusCodes.Status503ServiceUnavailable); }
    }
}
