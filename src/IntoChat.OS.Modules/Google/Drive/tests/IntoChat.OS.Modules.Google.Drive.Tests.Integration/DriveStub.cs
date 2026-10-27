using System.Collections.Concurrent;
using CultureInfo = System.Globalization.CultureInfo;
using NumberStyles = System.Globalization.NumberStyles;
using DateTimeStyles = System.Globalization.DateTimeStyles;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Testing.Conformance.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// Google Drive v3 on loopback. My Drive holds a Notes folder and a Google document; Notes holds a.txt. files.list pages by
// pageToken/nextPageToken and honours pageSize, orderBy (folder, name_natural, desc), "in parents", "name contains" and
// trashed; about answers the user and storageQuota (QuotaJson); alt=media honours Range (206). Seed adds a StorageSeed tree
// under My Drive in a fresh folder, with a folder shortcut, a file shortcut and a trashed file beside it.
internal sealed partial class DriveStub : IAsyncDisposable
{
    internal const string RootId = "root-id";
    private const string Folder = "application/vnd.google-apps.folder";
    private const string Shortcut = "application/vnd.google-apps.shortcut";

    private sealed record Entry(string Id, string Name, string MimeType, string? Parent, byte[]? Bytes,
        bool Trashed = false, string? TargetId = null, string? TargetMimeType = null)
    {
        public DateTimeOffset Modified { get; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly WebApplication _app;
    private int _next;

    private DriveStub(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
        foreach (var entry in new Entry[]
        {
            new(RootId, "My Drive", Folder, null, null),
            new("notes", "Notes", Folder, RootId, null),
            new("plan", "Plan", "application/vnd.google-apps.document", RootId, "exported plan"u8.ToArray()),
            new("a", "a.txt", "text/plain", "notes", "hello from drive"u8.ToArray()),
        }) { _items[entry.Id] = entry; }
    }

    public Uri Endpoint { get; }
    public string? LastAuthorization { get; private set; }
    public string? LastRange { get; private set; }

    // The about.get storageQuota answer: an object, or null for none.
    public object? QuotaJson { get; set; } = new { limit = "16106127360", usage = "1073741824", usageInDrive = "536870912", usageInDriveTrash = "0" };

    public static async Task<DriveStub> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        TestLogging.Apply(builder.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        DriveStub? stub = null;
        app.Use((context, next) =>
        {
            stub!.LastAuthorization = context.Request.Headers.Authorization.ToString();
            stub.LastRange = context.Request.Headers.Range.ToString();
            return next(context);
        });
        app.MapGet("/drive/about", () =>
        {
            object about = stub!.QuotaJson is { } quota
                ? new { user = new { emailAddress = "user@gmail.com" }, storageQuota = quota }
                : new { user = new { emailAddress = "user@gmail.com" } };
            return Results.Json(about);
        });
        app.MapGet("/drive/files", (string q, string? orderBy, int? pageSize, string? pageToken) => stub!.List(q, orderBy, pageSize, pageToken));
        app.MapGet("/drive/files/{id}", (string id, string? alt) => stub!.Get(id, alt));
        app.MapGet("/drive/files/{id}/export", (string id) => stub!.Find(id) is { Bytes: { } bytes } ? Results.Bytes(bytes, "text/plain") : NotFound());
        await app.StartAsync(ct);
        stub = new DriveStub(app, new Uri(app.Urls.Single() + "/drive/"));
        return stub;
    }

    // Adds seed under My Drive in a fresh folder and returns that folder's id.
    public string Seed(StorageSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var prefix = $"kit{Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture)}";
        var top = Add(new(prefix, prefix, Folder, RootId, null));
        var ids = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = top };
        foreach (var entry in seed.Entries)
        {
            var slash = entry.Path.LastIndexOf('/');
            var parent = ids[slash < 0 ? "" : entry.Path[..slash]];
            var name = slash < 0 ? entry.Path : entry.Path[(slash + 1)..];
            ids[entry.Path] = Add(new($"{prefix}-{ids.Count.ToString(CultureInfo.InvariantCulture)}", name,
                entry.Kind == ItemKind.Folder ? Folder : "text/plain", parent, entry.Kind == ItemKind.Folder ? null : entry.Bytes.ToArray()));
        }
        return top;
    }

    // Beside a seed: a shortcut to a folder, a shortcut to a file and a trashed file, in a fresh folder under My Drive.
    public (string Folder, string FolderShortcut, string FileShortcut, string Target) Shortcuts()
    {
        var prefix = $"links{Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture)}";
        var folder = Add(new(prefix, prefix, Folder, RootId, null));
        var target = Add(new($"{prefix}-target", "Target", Folder, folder, null));
        Add(new($"{prefix}-inside", "inside.txt", "text/plain", target, "inside"u8.ToArray()));
        var file = Add(new($"{prefix}-file", "file.txt", "text/plain", folder, "file"u8.ToArray()));
        var folderShortcut = Add(new($"{prefix}-to-folder", "To target", Shortcut, folder, null, TargetId: target, TargetMimeType: Folder));
        var fileShortcut = Add(new($"{prefix}-to-file", "To file", Shortcut, folder, null, TargetId: file, TargetMimeType: "text/plain"));
        Add(new($"{prefix}-trashed", "trashed.txt", "text/plain", folder, "gone"u8.ToArray(), Trashed: true));
        return (folder, folderShortcut, fileShortcut, target);
    }

    private string Add(Entry entry)
    {
        _items[entry.Id] = entry;
        return entry.Id;
    }

    private Entry? Find(string id) => _items.GetValueOrDefault(id == "root" ? RootId : id);

    private IResult List(string q, string? orderBy, int? pageSize, string? pageToken)
    {
        IEnumerable<Entry> items = _items.Values.Where(item => item.Id != RootId);
        if (Parent().Match(q) is { Success: true } parent)
        {
            var id = parent.Groups[1].Value == "root" ? RootId : parent.Groups[1].Value;
            items = items.Where(item => item.Parent == id);
        }
        if (Within().Match(q) is { Success: true } within)
        {
            var id = within.Groups[1].Value == "root" ? RootId : within.Groups[1].Value;
            items = items.Where(item => item.Parent == id);
        }
        if (Contains().Match(q) is { Success: true } contains)
        {
            var text = contains.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            items = items.Where(item => item.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        if (q.Contains("trashed = false", StringComparison.Ordinal)) { items = items.Where(item => !item.Trashed); }
        var descending = orderBy?.Contains("desc", StringComparison.Ordinal) == true;
        var sorted = items.OrderBy(item => orderBy?.StartsWith("folder", StringComparison.Ordinal) == true && item.MimeType != Folder ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        if (descending) { sorted.Reverse(); }
        var size = Math.Clamp(pageSize ?? 100, 1, 1000);
        var offset = int.TryParse(pageToken, NumberStyles.None, CultureInfo.InvariantCulture, out var token) ? token : 0;
        var page = sorted.Skip(offset).Take(size).Select(Json).ToList();
        var next = offset + size < sorted.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return next is null ? Results.Json(new { files = page }) : Results.Json(new { nextPageToken = next, files = page });
    }

    private IResult Get(string id, string? alt)
    {
        if (Find(id) is not { } item) { return NotFound(); }
        if (alt != "media") { return Results.Json(Json(item)); }
        // ASP.NET answers a Range header with 206 and the slice, as Drive does.
        return Results.Bytes(item.Bytes ?? [], item.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) ? "text/plain" : item.MimeType,
            enableRangeProcessing: true);
    }

    private static Dictionary<string, object?> Json(Entry item)
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["mimeType"] = item.MimeType,
            ["modifiedTime"] = item.Modified.ToString("O", CultureInfo.InvariantCulture),
            ["trashed"] = item.Trashed,
            ["parents"] = Parents(item),
            ["capabilities"] = new { canTrash = true, canDelete = false, canEdit = true },
        };
        if (item.MimeType != Folder && item.MimeType != Shortcut && item.Bytes is { } bytes)
        {
            json["size"] = bytes.Length.ToString(CultureInfo.InvariantCulture);
            json["md5Checksum"] = Convert.ToHexStringLower(MD5.HashData(bytes));
        }
        if (item.TargetId is { } target) { json["shortcutDetails"] = new { targetId = target, targetMimeType = item.TargetMimeType }; }
        return json;
    }

    private static string[] Parents(Entry item) => item.Parent is null ? [] : [item.Parent];

    private static IResult NotFound()
        => Results.Json(new { error = new { code = 404, message = "File not found.", errors = new[] { new { reason = "notFound" } } } }, statusCode: StatusCodes.Status404NotFound);

    [GeneratedRegex("^'([^']*)' in parents")]
    private static partial Regex Parent();

    [GeneratedRegex(" and '([^']*)' in parents")]
    private static partial Regex Within();

    [GeneratedRegex("name contains '((?:[^'\\\\]|\\\\.)*)'")]
    private static partial Regex Contains();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
