using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

public sealed record DevAppFile(string Path, string Content);

public sealed record DevAppPush(DevAppFile[] Files);

public sealed record DevDiagnostic(string Id, string Message, string File, int Line, string? Fix);

public sealed record DevAppResult(DevDiagnostic[] Diagnostics);

// PUT /dev/apps/{name} exists only while Topology is local. A failed compile leaves the previous bundle.
internal static class DevAppEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!DevAppAccess.IsLocal(configuration)) { return; }
        endpoints.MapPut("/dev/apps/{name}", Put);
    }

    internal static async Task<IResult> Put(string name, HttpContext http, FeatureCatalog catalog, DevAppGate gate, CancellationToken cancellationToken)
    {
        if (gate.Secret is not { Length: > 0 } secret) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        var rejection = DevAppAccess.Reject(http.Request.Host.Value, http.Request.ContentType, http.Connection.RemoteIpAddress, http.Request.Headers.Authorization, secret, gate.TrustedGateway);
        if (rejection is { } status) { return Results.StatusCode(status); }
        if (http.Request.ContentLength is > DevAppAccess.MaxBody) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        if (!IsAppName(name)) { return Results.BadRequest(new DevAppResult([new("name", "The app name must be lowercase letters, digits and dashes.", "", 0, null)])); }

        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
        {
            var buffer = new char[4096];
            var text = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (text.Length + read > DevAppAccess.MaxBody) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
                text.Append(buffer, 0, read);
            }
            body = text.ToString();
        }
        DevAppPush? push;
        try { push = JsonSerializer.Deserialize<DevAppPush>(body, Json); }
        catch (JsonException) { return Results.BadRequest(new DevAppResult([new("json", "The body must be a files array.", "", 0, null)])); }
        if (push?.Files is not { Length: > 0 }) { return Results.BadRequest(new DevAppResult([new("files", "Name at least one file.", "", 0, null)])); }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in push.Files)
        {
            var path = file.Path?.Replace('\\', '/');
            if (DevAppAccess.UnsafePath(path)) { return Results.BadRequest(new DevAppResult([new("path", "A file path must stay inside the app folder.", path ?? "", 0, null)])); }
            files[path!] = file.Content ?? "";
        }
        if (!files.TryGetValue(AppFiles.DeclarationPath, out var declaration))
        { return Results.BadRequest(new DevAppResult([new("app.cs", "The folder needs app.cs.", AppFiles.DeclarationPath, 0, null)])); }

        AppBundle bundle;
        try { bundle = FeatureFolder.ToBundle(name, files); }
        catch (InvalidDataException error) { return Results.BadRequest(new DevAppResult([new("app.cs", error.Message, AppFiles.DeclarationPath, 0, null)])); }
        // The URL name wins, and the publisher is local even when the folder claims os.
        if (!string.Equals(bundle.Name, name, StringComparison.Ordinal))
        { return Results.BadRequest(new DevAppResult([new("name", "The app.cs name does not match the URL.", AppFiles.DeclarationPath, 0, null)])); }

        var diagnostics = EmptyCatches(files);
        if (diagnostics.Length > 0) { return Results.Json(new DevAppResult(diagnostics), statusCode: StatusCodes.Status422UnprocessableEntity); }
        try { await catalog.AcceptLocal(bundle); }
        catch (InvalidOperationException error)
        { return Results.Json(new DevAppResult([new("compile", error.Message, "", 0, null)]), statusCode: StatusCodes.Status422UnprocessableEntity); }
        return Results.Json(new DevAppResult([]));
    }

    // The empty-catch rule in this tree is DB0022 (the plan's DB0004 id is not declared).
    internal static DevDiagnostic[] EmptyCatches(IReadOnlyDictionary<string, string> files)
    {
        var found = new List<DevDiagnostic>();
        foreach (var (path, source) in files)
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal)) { continue; }
            var root = CSharpSyntaxTree.ParseText(source, path: path).GetCompilationUnitRoot();
            foreach (var clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                if (clause.Block.Statements.Count > 0) { continue; }
                var line = clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                found.Add(new("DB0022", "A catch is empty or only swallows the error.", path, line, "Rethrow or record the error."));
            }
        }
        return [.. found];
    }

    private static bool IsAppName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
