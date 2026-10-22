using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using IntoChat.OS.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Google.Gmail.Testing;

public sealed record GoogleStubRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, string Body, string? Authorization);
public sealed record GoogleStubReply(int StatusCode, object? Body = null, TimeSpan? Delay = null, bool Abort = false,
    IReadOnlyDictionary<string, string>? Headers = null);
public sealed record GoogleStubMessage(string Id, string Subject, string Text, string From = "sender@example.test", string? Html = null, string? ThreadId = null);

// Only Google is replaced: consumers still run the real connection, OAuth and accounting services.
public sealed class GoogleStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TokenEndpointStub _tokens;
    private readonly HttpClient _tokenClient = new();
    private readonly ConcurrentQueue<GoogleStubRequest> _requests = new();
    private readonly ConcurrentDictionary<string, GoogleStubMessage> _messages = new(StringComparer.Ordinal);

    private GoogleStub(WebApplication app, TokenEndpointStub tokens, Uri origin)
    {
        _app = app;
        _tokens = tokens;
        Origin = origin;
        GmailEndpoint = new(origin, "gmail/v1/users/me/");
        ProfileEndpoint = new(GmailEndpoint, "profile");
        TokenEndpoint = new(origin, "token");
        Scope = "https://www.googleapis.com/auth/gmail.readonly";
    }

    public const string RevokedRefreshToken = TokenEndpointStub.RevokedRefreshToken;
    public Uri Origin { get; }
    public Uri GmailEndpoint { get; }
    public Uri ProfileEndpoint { get; }
    public Uri TokenEndpoint { get; }
    public string EmailAddress { get; set; } = "user@gmail.com";
    public string HistoryId { get; set; } = "100";
    public int ProfileStatus { get; set; } = StatusCodes.Status200OK;
    public string? LastProfileAuthorization { get; private set; }
    public DateTimeOffset WatchExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
    public string Scope { get => _tokens.Scope; set => _tokens.Scope = value; }
    public int RefreshExpiresIn { get => _tokens.RefreshExpiresIn; set => _tokens.RefreshExpiresIn = value; }
    public int Refreshes => _tokens.Refreshes;
    public string[] RefreshTokens => _tokens.RefreshTokens;
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Forms => _tokens.Forms;
    public IReadOnlyDictionary<string, string>? LastForm => _tokens.LastForm;
    public bool OmitRefreshScope { get; set; }
    public GoogleStubRequest[] Requests => [.. _requests];
    public ConcurrentQueue<GoogleStubReply> HistoryReplies { get; } = new();
    public ConcurrentQueue<GoogleStubReply> WatchReplies { get; } = new();
    public ConcurrentQueue<GoogleStubReply> DraftReplies { get; } = new();
    public ConcurrentQueue<GoogleStubReply> ListReplies { get; } = new();
    public ConcurrentDictionary<string, GoogleStubReply> MessageReplies { get; } = new(StringComparer.Ordinal);

    public void AddMessage(string id, string subject, string text, string from = "sender@example.test", string? html = null)
        => _messages[id] = new(id, subject, text, from, html);

    public void ClearRequests() { while (_requests.TryDequeue(out _)) { } }

    public static async Task<GoogleStub> StartAsync(CancellationToken ct)
    {
        var tokens = await TokenEndpointStub.StartAsync(ct);
        try
        {
            var builder = WebApplication.CreateBuilder();
            TestLogging.Apply(builder.Configuration);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            GoogleStub? stub = null;
            app.Use(async (context, next) =>
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                stub!._requests.Enqueue(new(context.Request.Method, context.Request.Path,
                    context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
                    body, context.Request.Headers.Authorization.ToString()));
                await next(context);
            });
            app.MapPost("/token", context => stub!.TokenAsync(context));
            app.MapGet("/gmail/v1/users/me/profile", (HttpContext context) =>
            {
                stub!.LastProfileAuthorization = context.Request.Headers.Authorization.ToString();
                return stub.ProfileStatus == 200
                    ? Results.Json(new { emailAddress = stub.EmailAddress, historyId = stub.HistoryId })
                    : Results.StatusCode(stub.ProfileStatus);
            });
            app.MapPost("/gmail/v1/users/me/watch", context => stub!.ReplyAsync(context, stub.WatchReplies,
                new { historyId = stub.HistoryId, expiration = stub.WatchExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }));
            app.MapPost("/gmail/v1/users/me/stop", () => Results.Ok());
            app.MapGet("/gmail/v1/users/me/history", context => stub!.ReplyAsync(context, stub.HistoryReplies,
                new { historyId = stub.HistoryId, history = Array.Empty<object>() }));
            app.MapGet("/gmail/v1/users/me/messages", context => stub!.ListAsync(context));
            app.MapGet("/gmail/v1/users/me/messages/{id}", (HttpContext context, string id) => stub!.MessageAsync(context, id));
            app.MapPost("/gmail/v1/users/me/drafts", context => stub!.ReplyAsync(context, stub.DraftReplies,
                new { id = "draft-1", message = new { id = "draft-message-1" } }));
            await app.StartAsync(ct);
            stub = new(app, tokens, new(app.Urls.Single() + "/"));
            return stub;
        }
        catch { await tokens.DisposeAsync(); throw; }
    }

    private async Task TokenAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokens.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal)),
        };
        using var response = await _tokenClient.SendAsync(request, context.RequestAborted);
        var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
        if (OmitRefreshScope && form["grant_type"] == "refresh_token" && response.IsSuccessStatusCode)
        {
            var json = JsonNode.Parse(body)!.AsObject();
            json.Remove("scope");
            body = json.ToJsonString();
        }
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    private async Task ListAsync(HttpContext context)
    {
        if (ListReplies.TryDequeue(out var reply)) { await WriteAsync(context, reply); return; }
        var take = int.TryParse(context.Request.Query["maxResults"], out var requested) ? requested : 25;
        var offset = int.TryParse(context.Request.Query["pageToken"], out var parsed) ? parsed : 0;
        var all = _messages.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        await context.Response.WriteAsJsonAsync(new
        {
            messages = all.Skip(offset).Take(take).Select(value => new { id = value.Id, threadId = value.ThreadId ?? value.Id }).ToArray(),
            nextPageToken = offset + take < all.Length ? (offset + take).ToString(CultureInfo.InvariantCulture) : null,
        }, context.RequestAborted);
    }

    private async Task MessageAsync(HttpContext context, string id)
    {
        if (MessageReplies.TryGetValue(id, out var reply)) { await WriteAsync(context, reply); return; }
        if (!_messages.TryGetValue(id, out var message)) { context.Response.StatusCode = 404; return; }
        var headers = new[]
        {
            new { name = "From", value = message.From },
            new { name = "To", value = EmailAddress },
            new { name = "Subject", value = message.Subject },
            new { name = "Date", value = "Thu, 22 Oct 2026 12:00:00 +0000" },
        };
        static object Body(string text) => new { size = Encoding.UTF8.GetByteCount(text), data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_') };
        var metadata = context.Request.Query["format"] == "metadata";
        object payload = message.Html is null
            ? new { mimeType = "text/plain", headers, body = metadata ? null : Body(message.Text) }
            : new { mimeType = "multipart/alternative", headers, parts = metadata ? Array.Empty<object>() : [new { mimeType = "text/plain", body = Body(message.Text) }, new { mimeType = "text/html", body = Body(message.Html) }] };
        await context.Response.WriteAsJsonAsync(new
        {
            id = message.Id,
            threadId = message.ThreadId ?? message.Id,
            historyId = HistoryId,
            snippet = message.Text,
            internalDate = "1792670400000",
            labelIds = new[] { "INBOX" },
            payload,
        }, context.RequestAborted);
    }

    private Task ReplyAsync(HttpContext context, ConcurrentQueue<GoogleStubReply> replies, object fallback)
        => WriteAsync(context, replies.TryDequeue(out var reply) ? reply : new(200, fallback));

    private static async Task WriteAsync(HttpContext context, GoogleStubReply reply)
    {
        if (reply.Delay is { } delay) { await Task.Delay(delay, context.RequestAborted); }
        if (reply.Abort) { context.Abort(); return; }
        context.Response.StatusCode = reply.StatusCode;
        if (reply.Headers is not null)
        { foreach (var pair in reply.Headers) { context.Response.Headers[pair.Key] = pair.Value; } }
        if (reply.Body is not null) { await context.Response.WriteAsJsonAsync(reply.Body, context.RequestAborted); }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _tokenClient.Dispose();
        await _tokens.DisposeAsync();
    }
}
