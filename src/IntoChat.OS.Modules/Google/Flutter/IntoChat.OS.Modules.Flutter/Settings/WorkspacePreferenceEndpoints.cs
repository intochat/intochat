using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Framework;
using IntoChat.OS.Apps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Flutter.Settings;

// Per-brain workspace preferences. Today that is the app a new tab opens, stored under {brain}/settings/workspace
// as a text field holding the app name. Any member may read and change it.
internal static class WorkspacePreferenceEndpoints
{
    public const string Default = "ino";

    // Copy of Desktop.Hidden in features/os/Desktop/Desktop.cs: background services and roots never appear
    // as apps a person can choose. WorkspacePreferenceFacts pins the two lists equal.
    internal static readonly HashSet<string> Hidden = ["desktop", "workspace", "router", "selfimprove", "circleroom", "usage"];

    internal sealed record Choice(string Name, string Title);

    internal sealed record Preferences(string NewTabApp, Choice[] Choices);

    internal sealed record Input(string? NewTabApp);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/preferences/workspace");
        group.MapGet("", async (IIntoChatOS brain, CancellationToken ct) => Results.Ok(await Read(brain, ct)));
        group.MapPut("", Write);
    }

    // Installed apps (App callers or calls through the app proxy) cannot change a person's preferences.
    internal static async Task<IResult> Write(Input? input, IIntoChatOS brain, CancellationToken ct)
    {
        if (CallerContextStamper.TryGet(out var caller) && (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy))
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var choices = await Choices(brain, ct);
        if (input?.NewTabApp is not { } name || !choices.Any(choice => choice.Name == name))
        { return Results.BadRequest(new { error = "Choose an installed app." }); }
        await brain.Get<ITextField>(Field()).SetValue(name).WaitAsync(ct);
        return Results.Ok(await Read(brain, ct));
    }

    private static string Field() => BrainScope.CurrentId() + "/settings/workspace/new-tab-app";

    private static async Task<Preferences> Read(IIntoChatOS brain, CancellationToken ct)
    {
        var choices = await Choices(brain, ct);
        var stored = (await brain.Get<ITextField>(Field()).Read().WaitAsync(ct)).Value;
        return new(choices.Any(choice => choice.Name == stored) ? stored! : Default, choices);
    }

    private static async Task<Choice[]> Choices(IIntoChatOS brain, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        var found = new List<Choice>();
        await foreach (var instance in brain.Kernel.Instances<IApp>(ct))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name || Hidden.Contains(name)) { continue; }
            var app = await brain.Get<IApp>(instance.Key).Read().WaitAsync(ct);
            if (!string.IsNullOrWhiteSpace(app.Manifest.Title) && app.MigrationTarget is null) { found.Add(new(name, app.Manifest.Title)); }
        }
        return [.. found.OrderBy(choice => choice.Name == Default ? 0 : 1).ThenBy(choice => choice.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
