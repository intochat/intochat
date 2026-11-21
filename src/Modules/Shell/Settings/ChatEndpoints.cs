using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flutter.Settings;

// Ino's chat list for Settings → Archived chats. Any member may list and unarchive. Titles are customer
// data, so installed apps and calls through the app proxy are refused on reads as well as writes.
internal static class ChatEndpoints
{
    internal sealed record Chat(string Id, string Title, DateTimeOffset UpdatedAt);

    internal sealed record ChatPage(Chat[] Chats, int Total);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = OsBrainRoutes.Group(endpoints, "/chats");
        group.MapGet("", List);
        group.MapPost("/{id}/unarchive", Unarchive);
    }

    internal static string IndexKey(string brain) => $"{brain}/{ISessionList.DefaultKey}";

    internal static async Task<IResult> List(HttpContext http, INeuronProxies brain, CancellationToken ct, bool archived = true, int skip = 0, int take = Sessions.MaxPage)
    {
        if (Refused(http)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        if (skip < 0 || take is < 1 or > Sessions.MaxPage) { return Results.BadRequest(new { error = "Page out of range." }); }
        var page = await Index(brain).List(archived, skip, take).WaitAsync(ct);
        return Results.Ok(new ChatPage([.. page.Entries.Select(entry => new Chat(entry.Id, entry.Title, entry.UpdatedAt))], page.Total));
    }

    internal static async Task<IResult> Unarchive(HttpContext http, string id, INeuronProxies brain, CancellationToken ct)
    {
        if (Refused(http)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var index = Index(brain);
        if (!Sessions.IsId(id) || await index.Get(id).WaitAsync(ct) is null)
        { return Results.NotFound(new { error = "That chat no longer exists." }); }
        await index.Unarchive(id).WaitAsync(ct);
        return Results.NoContent();
    }

    // No caller, or an installed app, may not read or change a person's chats.
    private static bool Refused(HttpContext http)
        => DigitalBrain.Runtime.Http.Surface.HttpCaller.Current(http) is not { } caller || DigitalBrain.Access.CallerRules.IsApp(caller, out _);

    private static ISessionList Index(INeuronProxies brain)
        => brain.Get<ISessionList>(IndexKey(BrainScope.CurrentId()));
}
