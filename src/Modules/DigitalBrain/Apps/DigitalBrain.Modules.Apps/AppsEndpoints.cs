using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Apps;

// The whole apps surface, one screenful: create and author are synchronous (one model call);
// implement and verify answer 202 and finish in the background; sharing is export/import.
internal static class AppsEndpoints
{
    public sealed record TextRequest(string? Text);
    public sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    public sealed record RollbackRequest(string Version);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);

        apps.MapGet("", (IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            var scope = BrainScope.CurrentId();
            var names = await brain.Get<IAppIndex>(scope).List().WaitAsync(ct);
            var items = new List<object>();
            foreach (var name in names)
            {
                var snapshot = await App(brain, name).Read().WaitAsync(ct);
                items.Add(new { name, title = snapshot.Manifest.Title, description = snapshot.Manifest.Description, status = snapshot.Status.ToString(), version = snapshot.Version });
            }
            return Results.Ok(items);
        }));

        apps.MapPost("", (TextRequest request, [FromServices] Creator creator, IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), null, request.Text, ct)));
        }));

        apps.MapGet("/{name}", (string name, IDigitalBrain brain, CancellationToken ct)
            => Respond(async () => Results.Ok(View(await App(brain, name).Read().WaitAsync(ct)))));

        apps.MapPost("/{name}/author", (string name, TextRequest request, [FromServices] Creator creator, IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), name, request.Text, ct)));
        }));

        apps.MapPost("/{name}/implement", (string name, TextRequest request, [FromServices] Creator creator, IDigitalBrain brain) => Respond(async () =>
        {
            var snapshot = await creator.Implement(brain, Key(name), request?.Text);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        apps.MapPost("/{name}/verify", (string name, HttpContext context, IDigitalBrain brain) => Respond(async () =>
        {
            var snapshot = await App(brain, name).BeginVerify();
            AppVerifyJob.Enqueue(context.RequestServices, Key(name));
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        apps.MapPut("/{name}/settings", (string name, SettingsRequest request, IDigitalBrain brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Configure(request?.Settings ?? [], request?.Connections ?? [])))));

        apps.MapPost("/{name}/rollback", (string name, RollbackRequest request, IDigitalBrain brain) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Version);
            return Results.Ok(View(await App(brain, name).Rollback(request.Version)));
        }));

        apps.MapPost("/{name}/ask", (string name, HttpRequest request, IDigitalBrain brain, CancellationToken ct)
            => Respond(() => Ask(App(brain, name), request, ct)));

        apps.MapGet("/{name}/export", (string name, IDigitalBrain brain, CancellationToken ct)
            => Respond(async () => Results.Ok(await App(brain, name).Export().WaitAsync(ct))));

        apps.MapPost("/import", (AppBundle bundle, IDigitalBrain brain) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentException.ThrowIfNullOrWhiteSpace(bundle.Name);
            return Results.Ok(View(await App(brain, bundle.Name).Import(bundle)));
        }));

        apps.MapDelete("/{name}", (string name, IDigitalBrain brain) => Respond(async () =>
        {
            await App(brain, name).Delete();
            return Results.Ok(new { deleted = name });
        }));
    }

    private static string Key(string name) => IAppV2.Key(BrainScope.CurrentId(), name);

    private static IAppV2 App(IDigitalBrain brain, string name) => brain.Get<IAppV2>(Key(name));

    // The wire view: the snapshot plus status as a string the shell can switch on.
    private static object View(AppSnapshotV2 snapshot) => new
    {
        name = snapshot.Name,
        manifest = snapshot.Manifest,
        spec = snapshot.Spec,
        files = snapshot.Files,
        settings = snapshot.Settings,
        connections = snapshot.Connections,
        version = snapshot.Version,
        status = snapshot.Status.ToString(),
        activity = snapshot.Activity,
        versions = snapshot.Versions,
        verdicts = snapshot.Verdicts,
    };

    private static async Task<IResult> Ask(IAppV2 app, HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var input = await reader.ReadToEndAsync(ct);
        var invocation = await app.Invoke(new(Guid.NewGuid(), "ask", input.Length == 0 ? "{}" : input)).WaitAsync(ct);
        var started = TimeProvider.System.GetTimestamp();
        while (invocation.Status == InvocationStatus.Pending && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, ct);
            invocation = await app.ReadInvocation(invocation.Id).WaitAsync(ct);
        }
        if (invocation.Status == InvocationStatus.Pending)
        { return Results.Json(new { error = "The app has not answered yet.", invocationId = invocation.Id }, statusCode: 408); }
        if (invocation.Status != InvocationStatus.Completed || invocation.Output is null)
        { return Results.UnprocessableEntity(new { error = invocation.Error ?? "The app returned no output.", invocationId = invocation.Id }); }
        try
        {
            using var output = JsonDocument.Parse(invocation.Output);
            return Results.Ok(output.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Results.Text(invocation.Output);
        }
    }

    private static async Task<IResult> Respond(Func<Task<IResult>> handler)
    {
        try { return await handler(); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        catch (KeyNotFoundException error) { return Results.NotFound(new { error = error.Message }); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return Results.UnprocessableEntity(new { error = error.Message }); }
    }
}
