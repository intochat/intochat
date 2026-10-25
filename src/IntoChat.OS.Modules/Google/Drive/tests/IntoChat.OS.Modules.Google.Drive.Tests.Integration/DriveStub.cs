using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// Google Drive v3 on loopback: My Drive holds a Notes folder and a Google document; Notes holds a.txt.
internal sealed partial class DriveStub : IAsyncDisposable
{
    private sealed record Item(string Id, string Name, string MimeType, string? Parent, string? Text);

    private static readonly Item[] Items =
    [
        new("root-id", "My Drive", "application/vnd.google-apps.folder", null, null),
        new("notes", "Notes", "application/vnd.google-apps.folder", "root-id", null),
        new("plan", "Plan", "application/vnd.google-apps.document", "root-id", "exported plan"),
        new("a", "a.txt", "text/plain", "notes", "hello from drive"),
    ];

    private readonly WebApplication _app;

    private DriveStub(WebApplication app, Uri endpoint) { _app = app; Endpoint = endpoint; }

    public Uri Endpoint { get; }
    public string? LastAuthorization { get; private set; }

    public static async Task<DriveStub> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        TestLogging.Apply(builder.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        DriveStub? stub = null;
        app.Use((context, next) => { stub!.LastAuthorization = context.Request.Headers.Authorization.ToString(); return next(context); });
        app.MapGet("/drive/about", () => Results.Json(new { user = new { emailAddress = "user@gmail.com" } }));
        app.MapGet("/drive/files", (string q) =>
        {
            var parent = Parent().Match(q).Groups[1].Value;
            parent = parent == "root" ? "root-id" : parent;
            return Results.Json(new { files = Items.Where(item => item.Parent == parent).Select(item => new { id = item.Id, name = item.Name, mimeType = item.MimeType }) });
        });
        app.MapGet("/drive/files/{id}", (string id, string? alt) => Find(id) is not { } item ? Results.NotFound()
            : alt == "media" ? Results.Text(item.Text ?? "")
            : Results.Json(new { id = item.Id, name = item.Name, mimeType = item.MimeType, parents = item.Parent is null ? Array.Empty<string>() : [item.Parent] }));
        app.MapGet("/drive/files/{id}/export", (string id) => Find(id) is { } item ? Results.Text(item.Text ?? "") : Results.NotFound());
        await app.StartAsync(ct);
        stub = new DriveStub(app, new Uri(app.Urls.Single() + "/drive/"));
        return stub;
    }

    private static Item? Find(string id) => Items.FirstOrDefault(item => item.Id == (id == "root" ? "root-id" : id));

    [GeneratedRegex("^'([^']*)' in parents")]
    private static partial Regex Parent();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
