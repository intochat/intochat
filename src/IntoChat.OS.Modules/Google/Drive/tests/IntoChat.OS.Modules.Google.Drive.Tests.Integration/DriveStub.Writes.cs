using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

internal sealed partial class DriveStub
{
    private readonly ConcurrentDictionary<string, Entry> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Status, string Reason)> _uploadFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Uri> _uploadLocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _malformedUploads = new(StringComparer.Ordinal);

    internal void RefuseUpload(string name, int status, string reason) => _uploadFailures[name] = (status, reason);
    internal void UploadLocation(string name, Uri location) => _uploadLocations[name] = location;
    internal void MalformedUploadResponse(string name) => _malformedUploads[name] = true;
    internal void RefuseCopy(string name)
    {
        var item = _items.Values.Single(item => item.Name == name);
        Add(item with { Copyable = false });
    }
    internal void RestrictMutations(string name)
    {
        var item = _items.Values.Single(item => item.Name == name);
        Add(item with { Mutable = false });
    }

    private static void MapWrites(WebApplication app, Func<DriveStub> get)
    {
        app.MapPost("/drive/files", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            var entry = get().NewEntry(body.RootElement);
            get().Add(entry);
            return Results.Json(Json(entry));
        });
        app.MapPatch("/drive/files/{id}", async (string id, HttpRequest request) =>
        {
            var stub = get();
            if (stub.Find(id) is not { } item) { return NotFound(); }
            using var body = await JsonDocument.ParseAsync(request.Body);
            if (body.RootElement.TryGetProperty("name", out var name)) { item = item with { Name = name.GetString()! }; }
            if (body.RootElement.TryGetProperty("trashed", out var trashed)) { item = item with { Trashed = trashed.GetBoolean() }; }
            if (request.Query["addParents"].ToString() is { Length: > 0 } parent) { item = item with { Parent = parent == "root" ? RootId : parent }; }
            stub.Add(item);
            return Results.Json(Json(item));
        });
        app.MapDelete("/drive/files/{id}", (string id) => get()._items.TryRemove(id, out _) ? Results.NoContent() : NotFound());
        app.MapPost("/drive/files/{id}/copy", async (string id, HttpRequest request) =>
        {
            var stub = get();
            if (stub.Find(id) is not { } item) { return NotFound(); }
            using var body = await JsonDocument.ParseAsync(request.Body);
            var copy = stub.NewEntry(body.RootElement) with { MimeType = item.MimeType, Bytes = item.Bytes };
            stub.Add(copy);
            return Results.Json(Json(copy));
        });
        app.MapMethods("/upload/drive/files/{id?}", ["POST", "PATCH"], async (string? id, HttpRequest request) =>
        {
            var stub = get();
            using var body = await JsonDocument.ParseAsync(request.Body);
            var item = id is null ? stub.NewEntry(body.RootElement) : stub.Find(id);
            if (item is null) { return NotFound(); }
            if (stub._uploadFailures.TryRemove(item.Name, out var failure))
            { return Results.Json(new { error = new { errors = new[] { new { reason = failure.Reason } } } }, statusCode: failure.Status); }
            var session = Guid.NewGuid().ToString("N");
            stub._uploads[session] = item;
            request.HttpContext.Response.Headers.Location = stub._uploadLocations.TryRemove(item.Name, out var location)
                ? location.AbsoluteUri : $"{request.Scheme}://{request.Host}/upload/session/{session}";
            return Results.Ok();
        });
        app.MapPut("/upload/session/{session}", async (string session, HttpRequest request) =>
        {
            var stub = get();
            if (!stub._uploads.TryRemove(session, out var item)) { return NotFound(); }
            using var content = new MemoryStream();
            await request.Body.CopyToAsync(content);
            item = item with { Bytes = content.ToArray() };
            stub.Add(item);
            if (stub._malformedUploads.TryRemove(item.Name, out _)) { return Results.Text("not-json", "application/json"); }
            return Results.Json(Json(item));
        });
    }

    private Entry NewEntry(JsonElement body)
    {
        var parent = body.TryGetProperty("parents", out var parents) ? parents[0].GetString() : RootId;
        return new Entry("new-" + Guid.NewGuid().ToString("N"), body.GetProperty("name").GetString()!,
            body.TryGetProperty("mimeType", out var mime) ? mime.GetString()! : "application/octet-stream",
            parent == "root" ? RootId : parent, []);
    }
}
