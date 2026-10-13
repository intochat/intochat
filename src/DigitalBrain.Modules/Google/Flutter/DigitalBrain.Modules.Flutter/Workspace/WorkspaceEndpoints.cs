using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flutter.Workspace;

internal static class WorkspaceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var brain = BrainRoutes.Group(endpoints);
        brain.MapGet("", (IDigitalBrain digitalBrain, CancellationToken ct)
            => Respond(async () => Results.Ok(await Workspace(digitalBrain).Read().WaitAsync(ct))));
        brain.MapPost("/show", (NeuronCommand input, IDigitalBrain digitalBrain, CancellationToken ct)
            => Respond(async () => Results.Ok(await Workspace(digitalBrain).Show(input.App, input.OperationId, input.ExpectedRevision).WaitAsync(ct))));
        brain.MapPost("/back", (NavigationCommand input, IDigitalBrain digitalBrain, CancellationToken ct)
            => Respond(async () => Results.Ok(await Workspace(digitalBrain).Back(input.OperationId, input.ExpectedRevision).WaitAsync(ct))));
        brain.MapPost("/home", (NavigationCommand input, IDigitalBrain digitalBrain, CancellationToken ct)
            => Respond(async () => Results.Ok(await Workspace(digitalBrain).Home(input.OperationId, input.ExpectedRevision).WaitAsync(ct))));
        brain.MapPost("/close", (NeuronCommand input, IDigitalBrain digitalBrain, CancellationToken ct)
            => Respond(async () => Results.Ok(await Workspace(digitalBrain).Close(input.App, input.OperationId, input.ExpectedRevision).WaitAsync(ct))));
        brain.MapPost("/reports", (ProblemReportInput input, IDigitalBrain digitalBrain, CancellationToken ct) => ProblemReportEndpoint.File(input, digitalBrain, ct));
    }

    private static IWorkspace Workspace(IDigitalBrain digitalBrain) => digitalBrain.Get<IWorkspace>(BrainScope.CurrentId());

    private static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RevisionConflictException error) { return Results.Conflict(new { error = error.Message, currentRevision = error.CurrentRevision }); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "The requested workspace item was not found." }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (TimeoutException) { return Results.Problem("The workspace operation timed out. Refresh before retrying.", statusCode: 503); }
    }

    internal sealed record NeuronCommand(string App, string OperationId, long ExpectedRevision);
    internal sealed record NavigationCommand(string OperationId, long ExpectedRevision);
}
