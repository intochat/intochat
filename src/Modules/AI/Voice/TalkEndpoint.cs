using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Voice;

// Full-duplex WebSocket endpoint for voice mode (GET /ai/talk?conversation={id}).
// Wire: client -> Kernel PCM16 LE mono 16 kHz; Kernel -> client PCM16 LE mono 24 kHz; text frames are TalkJson TalkEvent.
internal static class TalkEndpoint
{
    internal const int ClientInputRate = 16_000;
    internal const int ClientOutputRate = 24_000;
    internal const string BrainTurnToolName = TalkToolDispatcher.BrainTurnToolName;
    internal const int MaxBrainTurnCallsPerSession = TalkToolDispatcher.MaxBrainTurnCallsPerSession;
    private const int HistorySaidCount = 20;

    private static readonly TimeSpan MaxSessionDuration = TimeSpan.FromMinutes(30);
    private static readonly string[] ForbiddenQueryAuthParameters = ["access_token", "token", "authorization", "bearer"];

    internal const string DefaultPersona =
        "You are a helpful voice assistant. Keep spoken answers clear, natural, and concise. "
        + "When the user asks you to run OS tools, inspect memory, search the web, check files, or open/build apps, "
        + "briefly acknowledge in 3 to 5 natural words (for example, \"On it, checking now...\") while calling the 'run_brain_turn' tool "
        + "with their request, and then speak a concise summary of the result.";

    public static async Task<IResult> MintTicketAsync(HttpContext http, TalkTicketStore tickets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tickets);

        http.Response.Headers.CacheControl = "no-store";
        if (AIModule.StampedCaller(http) is not { } caller) { return Results.Unauthorized(); }
        if (!AIModule.IsDirectPersonCaller(caller)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        string? rawConversation = null;
        if (http.Request.ContentLength is > 0 || http.Request.HasJsonContentType())
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync(http.Request.Body, TalkJson.Default.TalkTicketRequest, ct).ConfigureAwait(false);
                rawConversation = body?.Conversation;
            }
            catch (JsonException)
            {
                return Results.Problem("Supply a JSON body with a 'conversation' id.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (string.IsNullOrWhiteSpace(rawConversation)) { rawConversation = http.Request.Query["conversation"].ToString(); }
        if (string.IsNullOrWhiteSpace(rawConversation) || !ConversationId.TryParse(rawConversation, out var conversationId))
        {
            return Results.Problem("A valid conversation id is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var (ticket, expiresInSeconds) = tickets.Issue(caller, conversationId);
            return Results.Json(new TalkTicketResponse(ticket, expiresInSeconds), TalkJson.Default.TalkTicketResponse);
        }
        catch (InvalidOperationException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status429TooManyRequests);
        }
    }

    public static async Task Run(
        HttpContext http,
        INeuronProxies brain,
        IVoiceClients voice,
        IIntoChatOSBrains brains,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(brains);

        if (!TryValidateUpgradeRequest(http, out var conversationId, out var caller, out var ticketCallerScope))
        {
            return;
        }

        using var _ = ticketCallerScope;
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("IntoChat.OS.Modules.AI.Voice.TalkEndpoint")
            ?? OsConsoleLog.For<TalkEvent>();

        var talk = await voice.Talk(caller!.Brain.Value, conversationId, ct).ConfigureAwait(false);
        if (talk.Client is null)
        {
            using var errorTransport = new TalkWebSocketTransport(await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false));
            await SendErrorAndCloseAsync(errorTransport, talk.Unavailable ?? "Voice mode is unavailable.", ct).ConfigureAwait(false);
            return;
        }

        using var realtimeClient = talk.Client;
        var conversation = brain.Get<IConversation>(new NeuronId(caller.Brain, IConversation.Type, IConversation.KeyOf(conversationId)));
        var instructions = talk.RecordsItself ? null : await BuildCloudInstructionsAsync(conversation, logger, ct).ConfigureAwait(false);
        var voicePersona = await VoiceSettings.ReadVoicePersonaAsync(brain, caller.Brain, ct).ConfigureAwait(false);

        var sessionOptions = new RealtimeSessionOptions
        {
            Model = talk.Model?.Value,
            Voice = voicePersona,
            InputAudioFormat = new RealtimeAudioFormat("audio/pcm", talk.InputRate),
            OutputAudioFormat = new RealtimeAudioFormat("audio/pcm", ClientOutputRate),
            VoiceActivityDetection = new VoiceActivityDetectionOptions { Enabled = true, AllowInterruption = true },
            Instructions = instructions,
            Tools = talk.RecordsItself ? null : [TalkToolDispatcher.CreateBrainTurnTool()],
            ToolMode = talk.RecordsItself ? null : ChatToolMode.Auto,
        };

        using var transport = new TalkWebSocketTransport(await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false));
        using var sessionLimitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionLimitCts.CancelAfter(MaxSessionDuration);

        IRealtimeClientSession session;
        try
        {
            session = await realtimeClient.CreateSessionAsync(sessionOptions, sessionLimitCts.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, "opening realtime voice session");
            await SendErrorAndCloseAsync(transport, error.Message, ct).ConfigureAwait(false);
            return;
        }

        await using (session.ConfigureAwait(false))
        {
            var clock = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            var bridge = new TalkSessionBridge(transport, session, conversation, brains, caller.Brain.Value, talk, clock, logger);
            await bridge.RunAsync(sessionLimitCts.Token).ConfigureAwait(false);
        }
    }

    internal static AITool CreateBrainTurnTool() => TalkToolDispatcher.CreateBrainTurnTool();

    internal static bool HasForbiddenQueryAuthToken(IQueryCollection query)
        => ForbiddenQueryAuthParameters.Any(query.ContainsKey);

    internal static bool IsOriginAllowed(HttpRequest request, KernelCorsOptions? cors)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0) { return true; }
        if (origins.Count > 1) { return false; }

        var rawOrigin = origins[0];
        if (string.IsNullOrWhiteSpace(rawOrigin)) { return true; }

        var normalized = NormalizeOrigin(rawOrigin);
        if (normalized is null) { return false; }

        var ownOrigin = request.Host.HasValue ? NormalizeOrigin($"{request.Scheme}://{request.Host.Value}") : null;
        if (ownOrigin is not null && string.Equals(normalized, ownOrigin, StringComparison.OrdinalIgnoreCase)) { return true; }

        var configuredOrigin = !string.IsNullOrWhiteSpace(cors?.AllowedOrigin) ? NormalizeOrigin(cors.AllowedOrigin) : null;
        return configuredOrigin is not null && string.Equals(normalized, configuredOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateUpgradeRequest(
        HttpContext http,
        out ConversationId conversationId,
        out Caller? caller,
        out IDisposable? ticketCallerScope)
    {
        conversationId = default;
        caller = null;
        ticketCallerScope = null;

        if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = StatusCodes.Status400BadRequest; return false; }
        if (HasForbiddenQueryAuthToken(http.Request.Query)) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return false; }

        var cors = http.RequestServices.GetService<IOptions<KernelCorsOptions>>()?.Value;
        if (!IsOriginAllowed(http.Request, cors)) { http.Response.StatusCode = StatusCodes.Status403Forbidden; return false; }

        var rawConversation = http.Request.Query["conversation"].ToString();
        if (string.IsNullOrWhiteSpace(rawConversation) || !ConversationId.TryParse(rawConversation, out conversationId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        var stampedCaller = AIModule.StampedCaller(http);
        var rawTicket = http.Request.Query["ticket"].ToString();
        if (!string.IsNullOrWhiteSpace(rawTicket))
        {
            var ticketStore = http.RequestServices.GetService<TalkTicketStore>();
            if (ticketStore is null || !ticketStore.TryRedeem(rawTicket, conversationId, out var redeemedCaller))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return false;
            }

            if (stampedCaller is not null && !SamePrincipal(stampedCaller, redeemedCaller))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return false;
            }

            caller = redeemedCaller;
            if (stampedCaller is null)
            {
                ticketCallerScope = http.RequestServices.GetRequiredService<EdgeAuthority>().Enter(caller);
            }
        }
        else if (stampedCaller is not null)
        {
            caller = stampedCaller;
        }
        else
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            ticketCallerScope?.Dispose();
            ticketCallerScope = null;
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        return true;
    }

    private static async Task SendErrorAndCloseAsync(TalkWebSocketTransport transport, string reason, CancellationToken ct)
    {
        await transport.SendEventAsync(new TalkError(reason), ct).ConfigureAwait(false);
        await transport.CloseSafelyAsync(WebSocketCloseStatus.InternalServerError, reason).ConfigureAwait(false);
    }

    private static bool SamePrincipal(Caller left, Caller right)
        => left.Brain == right.Brain
            && string.Equals(left.For?.Principal, right.For?.Principal, StringComparison.Ordinal)
            && string.Equals(left.For?.Account, right.For?.Account, StringComparison.Ordinal);

    private static string? NormalizeOrigin(string raw)
    {
        if (!Uri.TryCreate(raw.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Length > 1)
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme.ToLowerInvariant()}://{host}" : $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";
    }

    private static async Task<string> BuildCloudInstructionsAsync(IConversation conversation, ILogger logger, CancellationToken ct)
    {
        var sb = new StringBuilder(DefaultPersona);
        try
        {
            var view = await conversation.Read().WaitAsync(ct).ConfigureAwait(false);
            var recent = view.Entries.OfType<Said>().TakeLast(HistorySaidCount).ToArray();
            if (recent.Length > 0)
            {
                sb.AppendLine().AppendLine().AppendLine("Recent conversation context:");
                foreach (var entry in recent)
                {
                    sb.Append(entry.By is Participant.Human ? "User" : "Assistant").Append(": ").AppendLine(entry.Text);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, "reading conversation history for cloud realtime instructions");
        }

        return sb.ToString();
    }
}
